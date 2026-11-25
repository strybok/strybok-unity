using System;
using System.Threading;
using UnityEngine;

namespace Strybok
{
    /// <summary>
    /// Keeps the two values of the deferred-link check (an attempt counter and a "done" flag)
    /// in <c>PlayerPrefs</c>, which are removed with the app's data. <c>StrybokSdk</c> uses it
    /// unless the options carry another <see cref="IDeferredLinkStore"/>.
    ///
    /// <c>PlayerPrefs</c> may only be used on the main thread: create the store there. Calls
    /// from another thread are sent to the main thread and wait for it. Every write is followed
    /// by <c>PlayerPrefs.Save()</c>, so the value survives a crash right after it.
    /// </summary>
    public sealed class PlayerPrefsDeferredLinkStore : IDeferredLinkStore
    {
        private readonly SynchronizationContext _mainThread;

        /// <summary>Call on the main thread.</summary>
        public PlayerPrefsDeferredLinkStore()
            : this(SynchronizationContext.Current)
        {
        }

        /// <param name="mainThread">Unity's main-thread context; null to call <c>PlayerPrefs</c> directly.</param>
        public PlayerPrefsDeferredLinkStore(SynchronizationContext mainThread)
        {
            _mainThread = mainThread;
        }

        public string Read(string key)
        {
            string value = null;
            OnMainThread(() => value = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key, null) : null);
            return value;
        }

        public void Write(string key, string value)
        {
            OnMainThread(() =>
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
            });
        }

        private void OnMainThread(Action action)
        {
            if (_mainThread == null || SynchronizationContext.Current == _mainThread)
            {
                action();
                return;
            }

            // Send waits for the main thread; an exception there must reach the caller, which
            // treats it as a failed read or write.
            Exception failure = null;
            _mainThread.Send(
                _ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                },
                null);
            if (failure != null) throw new InvalidOperationException("PlayerPrefs failed", failure);
        }
    }
}
