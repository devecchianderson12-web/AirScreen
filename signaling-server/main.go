// DualScreen signaling server.
//
// It only introduces peers; it never sees video. Hosts (PCs) prove ownership of their HostId by signing a
// server challenge with their identity key (HostId = first 16 bytes of SHA-256(SubjectPublicKeyInfo)).
// Phones join a host by HostId (already paired) or by the hash of the PC's current 6-digit pairing code.
// The server then relays WebRTC offers/answers/ICE candidates. Offers are signed by the host, so a malicious
// server cannot man-in-the-middle the DTLS session. Optionally it issues short-lived TURN credentials using the
// coturn "REST API" shared-secret scheme (use-auth-secret).
package main

import (
	"crypto/ecdsa"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"log"
	"math/big"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type config struct {
	Addr       string
	TLSCert    string
	TLSKey     string
	TurnURLs   []string
	TurnSecret string
	TurnTTL    time.Duration
	MaxPeers   int
}

type msg map[string]any

type conn struct {
	ws      *websocket.Conn
	mu      sync.Mutex
	id      string
	isHost  bool
	hostID  string // for hosts: own id; for peers: joined host
	nonce   string
	limiter *rateLimiter
}

func (c *conn) send(m msg) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	_ = c.ws.SetWriteDeadline(time.Now().Add(10 * time.Second))
	return c.ws.WriteJSON(m)
}

type hub struct {
	cfg      config
	mu       sync.Mutex
	hosts    map[string]*conn // hostId -> host connection
	pairKeys map[string]string
	peers    map[string]*conn // peerId -> phone connection
}

func newHub(cfg config) *hub {
	return &hub{cfg: cfg, hosts: map[string]*conn{}, pairKeys: map[string]string{}, peers: map[string]*conn{}}
}

var upgrader = websocket.Upgrader{
	ReadBufferSize:  16 * 1024,
	WriteBufferSize: 16 * 1024,
	CheckOrigin:     func(r *http.Request) bool { return true }, // native clients only; auth is cryptographic
}

func randomID(n int) string {
	b := make([]byte, n)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func (h *hub) serveWS(w http.ResponseWriter, r *http.Request) {
	ws, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	ws.SetReadLimit(256 * 1024)
	c := &conn{ws: ws, id: randomID(8), nonce: randomID(16), limiter: newRateLimiter(30, time.Second)}
	defer h.drop(c)

	_ = ws.SetReadDeadline(time.Now().Add(90 * time.Second))
	ws.SetPongHandler(func(string) error { return ws.SetReadDeadline(time.Now().Add(90 * time.Second)) })
	go func() {
		t := time.NewTicker(30 * time.Second)
		defer t.Stop()
		for range t.C {
			c.mu.Lock()
			err := ws.WriteControl(websocket.PingMessage, nil, time.Now().Add(5*time.Second))
			c.mu.Unlock()
			if err != nil {
				return
			}
		}
	}()

	if err := c.send(msg{"t": "challenge", "nonce": c.nonce}); err != nil {
		return
	}
	for {
		var m msg
		if err := ws.ReadJSON(&m); err != nil {
			return
		}
		_ = ws.SetReadDeadline(time.Now().Add(90 * time.Second))
		if !c.limiter.allow() {
			_ = c.send(msg{"t": "error", "error": "rate-limited"})
			continue
		}
		if err := h.handle(c, m); err != nil {
			_ = c.send(msg{"t": "error", "error": err.Error()})
		}
	}
}

func str(m msg, k string) string {
	if v, ok := m[k].(string); ok {
		return v
	}
	return ""
}

func (h *hub) handle(c *conn, m msg) error {
	switch str(m, "t") {
	case "hello-host":
		hostID, err := verifyHost(str(m, "hostId"), str(m, "cert"), str(m, "sig"), c.nonce)
		if err != nil {
			return err
		}
		h.mu.Lock()
		if old, ok := h.hosts[hostID]; ok && old != c {
			_ = old.ws.Close() // newest authenticated connection wins (PC reconnected)
		}
		c.isHost, c.hostID = true, hostID
		h.hosts[hostID] = c
		h.mu.Unlock()
		log.Printf("host %s registered", hostID)
		return c.send(msg{"t": "registered", "iceServers": h.iceServers(hostID)})

	case "set-pair-key":
		if !c.isHost {
			return errors.New("not-a-host")
		}
		key := str(m, "key")
		h.mu.Lock()
		for k, v := range h.pairKeys {
			if v == c.hostID {
				delete(h.pairKeys, k)
			}
		}
		if len(key) == 64 {
			h.pairKeys[key] = c.hostID
		}
		h.mu.Unlock()
		return nil

	case "join":
		if c.isHost {
			return errors.New("host-cannot-join")
		}
		h.mu.Lock()
		hostID := str(m, "hostId")
		if hostID == "" {
			hostID = h.pairKeys[str(m, "pairKey")]
		}
		host := h.hosts[hostID]
		if host == nil || len(h.peers) >= h.cfg.MaxPeers {
			h.mu.Unlock()
			return errors.New("host-offline")
		}
		c.hostID = hostID
		h.peers[c.id] = c
		h.mu.Unlock()
		if err := c.send(msg{"t": "joined", "hostId": hostID, "iceServers": h.iceServers(c.id)}); err != nil {
			return err
		}
		return host.send(msg{"t": "peer", "peerId": c.id})

	case "signal":
		data, ok := m["data"].(map[string]any)
		if !ok {
			return errors.New("bad-signal")
		}
		h.mu.Lock()
		defer h.mu.Unlock()
		if c.isHost {
			peer := h.peers[str(m, "to")]
			if peer == nil || peer.hostID != c.hostID {
				return errors.New("unknown-peer")
			}
			go peer.send(msg{"t": "signal", "data": data})
			return nil
		}
		host := h.hosts[c.hostID]
		if host == nil {
			return errors.New("host-offline")
		}
		go host.send(msg{"t": "signal", "from": c.id, "data": data})
		return nil
	}
	return errors.New("unknown-message")
}

func (h *hub) drop(c *conn) {
	_ = c.ws.Close()
	h.mu.Lock()
	defer h.mu.Unlock()
	if c.isHost {
		if h.hosts[c.hostID] == c {
			delete(h.hosts, c.hostID)
			for k, v := range h.pairKeys {
				if v == c.hostID {
					delete(h.pairKeys, k)
				}
			}
			log.Printf("host %s left", c.hostID)
		}
		return
	}
	if _, ok := h.peers[c.id]; ok {
		delete(h.peers, c.id)
		if host := h.hosts[c.hostID]; host != nil {
			go host.send(msg{"t": "peer-left", "peerId": c.id})
		}
	}
}

// verifyHost checks that cert's public key hashes to hostId and that sig signs "DSHOST1|hostId|nonce".
func verifyHost(hostID, certB64, sigB64, nonce string) (string, error) {
	der, err := base64.StdEncoding.DecodeString(certB64)
	if err != nil {
		return "", errors.New("bad-cert")
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		return "", errors.New("bad-cert")
	}
	pub, ok := cert.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return "", errors.New("unsupported-key")
	}
	spkiHash := sha256.Sum256(cert.RawSubjectPublicKeyInfo)
	if hex.EncodeToString(spkiHash[:16]) != strings.ToLower(hostID) {
		return "", errors.New("host-id-mismatch")
	}
	sig, err := base64.StdEncoding.DecodeString(sigB64)
	if err != nil || len(sig) != 64 {
		return "", errors.New("bad-signature")
	}
	digest := sha256.Sum256([]byte("DSHOST1|" + hostID + "|" + nonce))
	r, s := new(big.Int).SetBytes(sig[:32]), new(big.Int).SetBytes(sig[32:])
	if !ecdsa.Verify(pub, digest[:], r, s) {
		return "", errors.New("bad-signature")
	}
	return strings.ToLower(hostID), nil
}

func (h *hub) iceServers(user string) []map[string]string {
	if len(h.cfg.TurnURLs) == 0 || h.cfg.TurnSecret == "" {
		return []map[string]string{}
	}
	username := time.Now().Add(h.cfg.TurnTTL).Unix()
	name := strings.Join([]string{itoa(username), user}, ":")
	mac := hmac.New(sha1.New, []byte(h.cfg.TurnSecret))
	mac.Write([]byte(name))
	cred := base64.StdEncoding.EncodeToString(mac.Sum(nil))
	out := []map[string]string{}
	for _, u := range h.cfg.TurnURLs {
		out = append(out, map[string]string{"urls": u, "username": name, "credential": cred})
	}
	return out
}

func itoa(v int64) string { return big.NewInt(v).String() }

type rateLimiter struct {
	mu     sync.Mutex
	tokens float64
	max    float64
	rate   float64
	last   time.Time
}

func newRateLimiter(max int, per time.Duration) *rateLimiter {
	return &rateLimiter{tokens: float64(max), max: float64(max), rate: float64(max) / per.Seconds(), last: time.Now()}
}

func (l *rateLimiter) allow() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := time.Now()
	l.tokens += now.Sub(l.last).Seconds() * l.rate
	if l.tokens > l.max {
		l.tokens = l.max
	}
	l.last = now
	if l.tokens < 1 {
		return false
	}
	l.tokens--
	return true
}

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func main() {
	var cfg config
	flag.StringVar(&cfg.Addr, "addr", env("DS_ADDR", ":8443"), "listen address")
	flag.StringVar(&cfg.TLSCert, "cert", env("DS_TLS_CERT", ""), "TLS certificate (PEM); empty = plain HTTP behind a TLS proxy")
	flag.StringVar(&cfg.TLSKey, "key", env("DS_TLS_KEY", ""), "TLS private key (PEM)")
	turn := flag.String("turn", env("DS_TURN_URLS", ""), "comma separated TURN URLs, e.g. turn:turn.example.com:3478?transport=udp")
	flag.StringVar(&cfg.TurnSecret, "turn-secret", env("DS_TURN_SECRET", ""), "coturn static-auth-secret")
	flag.DurationVar(&cfg.TurnTTL, "turn-ttl", 12*time.Hour, "TURN credential lifetime")
	flag.IntVar(&cfg.MaxPeers, "max-peers", 1000, "maximum concurrent phones")
	flag.Parse()
	if *turn != "" {
		cfg.TurnURLs = strings.Split(*turn, ",")
	}

	h := newHub(cfg)
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", h.serveWS)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		h.mu.Lock()
		n, p := len(h.hosts), len(h.peers)
		h.mu.Unlock()
		_ = json.NewEncoder(w).Encode(map[string]int{"hosts": n, "peers": p})
	})
	srv := &http.Server{Addr: cfg.Addr, Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	log.Printf("DualScreen signaling server on %s (TURN: %v)", cfg.Addr, len(cfg.TurnURLs) > 0)
	var err error
	if cfg.TLSCert != "" {
		err = srv.ListenAndServeTLS(cfg.TLSCert, cfg.TLSKey)
	} else {
		err = srv.ListenAndServe()
	}
	log.Fatal(err)
}
