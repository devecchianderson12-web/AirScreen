package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/hex"
	"math/big"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func hostIdentity(t *testing.T) (*ecdsa.PrivateKey, string, string) {
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tmpl := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "test"},
		NotBefore: time.Now(), NotAfter: time.Now().Add(time.Hour)}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	cert, _ := x509.ParseCertificate(der)
	h := sha256.Sum256(cert.RawSubjectPublicKeyInfo)
	return key, hex.EncodeToString(h[:16]), base64.StdEncoding.EncodeToString(der)
}

func sign(key *ecdsa.PrivateKey, text string) string {
	d := sha256.Sum256([]byte(text))
	r, s, _ := ecdsa.Sign(rand.Reader, key, d[:])
	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return base64.StdEncoding.EncodeToString(sig)
}

func dial(t *testing.T, url string) (*websocket.Conn, msg) {
	ws, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		t.Fatal(err)
	}
	var m msg
	if err := ws.ReadJSON(&m); err != nil || m["t"] != "challenge" {
		t.Fatalf("expected challenge, got %v %v", m, err)
	}
	return ws, m
}

func read(t *testing.T, ws *websocket.Conn) msg {
	_ = ws.SetReadDeadline(time.Now().Add(3 * time.Second))
	var m msg
	if err := ws.ReadJSON(&m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestHostPeerRendezvous(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(newHub(config{MaxPeers: 10, TurnURLs: []string{"turn:x:3478"}, TurnSecret: "s", TurnTTL: time.Hour}).serveWS))
	defer srv.Close()
	url := "ws" + strings.TrimPrefix(srv.URL, "http")

	key, hostID, cert := hostIdentity(t)
	host, ch := dial(t, url)
	_ = host.WriteJSON(msg{"t": "hello-host", "hostId": hostID, "cert": cert, "sig": sign(key, "DSHOST1|"+hostID+"|"+ch["nonce"].(string))})
	if m := read(t, host); m["t"] != "registered" {
		t.Fatalf("host not registered: %v", m)
	}
	pairKey := strings.Repeat("a", 64)
	_ = host.WriteJSON(msg{"t": "set-pair-key", "key": pairKey})
	time.Sleep(50 * time.Millisecond)

	phone, _ := dial(t, url)
	_ = phone.WriteJSON(msg{"t": "join", "pairKey": pairKey})
	if m := read(t, phone); m["t"] != "joined" || m["hostId"] != hostID {
		t.Fatalf("phone not joined: %v", m)
	}
	peer := read(t, host)
	if peer["t"] != "peer" {
		t.Fatalf("host did not get peer: %v", peer)
	}
	_ = host.WriteJSON(msg{"t": "signal", "to": peer["peerId"], "data": map[string]any{"kind": "offer", "sdp": "x"}})
	if m := read(t, phone); m["t"] != "signal" {
		t.Fatalf("phone did not get offer: %v", m)
	}
	_ = phone.WriteJSON(msg{"t": "signal", "data": map[string]any{"kind": "answer", "sdp": "y"}})
	if m := read(t, host); m["t"] != "signal" || m["from"] != peer["peerId"] {
		t.Fatalf("host did not get answer: %v", m)
	}
}

func TestRejectsForgedHost(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(newHub(config{MaxPeers: 10}).serveWS))
	defer srv.Close()
	url := "ws" + strings.TrimPrefix(srv.URL, "http")
	_, hostID, cert := hostIdentity(t)
	other, _, _ := hostIdentity(t)
	host, ch := dial(t, url)
	_ = host.WriteJSON(msg{"t": "hello-host", "hostId": hostID, "cert": cert, "sig": sign(other, "DSHOST1|"+hostID+"|"+ch["nonce"].(string))})
	if m := read(t, host); m["t"] != "error" || m["error"] != "bad-signature" {
		t.Fatalf("forged host accepted: %v", m)
	}
}

func TestJoinUnknownHost(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(newHub(config{MaxPeers: 10}).serveWS))
	defer srv.Close()
	phone, _ := dial(t, "ws"+strings.TrimPrefix(srv.URL, "http"))
	_ = phone.WriteJSON(msg{"t": "join", "hostId": "deadbeef"})
	if m := read(t, phone); m["error"] != "host-offline" {
		t.Fatalf("unexpected: %v", m)
	}
}
