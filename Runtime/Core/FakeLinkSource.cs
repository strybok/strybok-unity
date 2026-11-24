using System;
using System.Threading.Tasks;

namespace Strybok.Testing
{
    /// <summary>In-memory <see cref="ILinkSource"/> to simulate incoming links in tests.</summary>
    public sealed class FakeLinkSource : ILinkSource
    {
        private readonly object _gate = new object();
        private Action<string> _handlers;

        public FakeLinkSource(string initialLink = null, bool replayInitialLinkOnStream = false)
        {
            InitialLink = initialLink;
            ReplayInitialLinkOnStream = replayInitialLinkOnStream;
        }

        /// <summary>Link reported as the one that launched the app.</summary>
        public string InitialLink { get; set; }

        /// <summary>Mimics platforms that also report the launch link as a regular link.</summary>
        public bool ReplayInitialLinkOnStream { get; }

        public bool HasListener
        {
            get { lock (_gate) return _handlers != null; }
        }

        public event Action<string> UrlReceived
        {
            add
            {
                bool first;
                lock (_gate)
                {
                    first = _handlers == null;
                    _handlers += value;
                }
                string initial = InitialLink;
                if (first && ReplayInitialLinkOnStream && initial != null && value != null) value(initial);
            }
            remove
            {
                lock (_gate) _handlers -= value;
            }
        }

        /// <summary>Simulates the OS delivering <paramref name="url"/> to the running app.</summary>
        public void Emit(string url)
        {
            Action<string> handlers;
            lock (_gate) handlers = _handlers;
            if (handlers != null) handlers(url);
        }

        public Task<string> GetInitialLinkAsync()
        {
            return Task.FromResult(InitialLink);
        }
    }
}
