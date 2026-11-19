using System;
using Microsoft.Extensions.Logging;
using DualScreen.Core;

namespace DualScreen.Remote;

/// <summary>
/// Forwards SIPSorcery (WebRTC/ICE/TURN) logs into DualScreen's log file so remote-connection failures can be
/// diagnosed: warnings from everything, plus debug details from the ICE channel (STUN/TURN allocation, checks).
/// Must be installed before any SIPSorcery type is used (its loggers are created in static initialisers).
/// </summary>
public sealed class SipLogBridge : ILoggerFactory, ILoggerProvider
{
    public static void Install() => SIPSorcery.LogFactory.Set(new SipLogBridge());

    public ILogger CreateLogger(string categoryName) => new Bridge(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class Bridge : ILogger
    {
        private readonly string _category;
        private readonly bool _verbose;

        public Bridge(string category)
        {
            _category = category[(category.LastIndexOf('.') + 1)..];
            _verbose = category.Contains("RtpIceChannel") || category.Contains("IceServer") || category.Contains("RTCPeerConnection");
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= (_verbose ? LogLevel.Debug : LogLevel.Warning);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string msg = $"[webrtc:{_category}] {formatter(state, ex)}";
            if (level >= LogLevel.Warning) Core.Log.Warn(msg + (ex != null ? " " + ex.Message : ""));
            else Core.Log.Info(msg);
        }
    }
}
