using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Strybok
{
    /// <summary>
    /// <see cref="IHttpTransport"/> on top of <c>UnityWebRequest</c>. No coroutine or
    /// GameObject is needed: completion is observed through
    /// <c>AsyncOperation.completed</c>.
    ///
    /// Create it on Unity's main thread. <c>UnityWebRequest</c> only works there, so calls
    /// made from other threads are posted to the main thread first.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        private readonly SynchronizationContext _mainThread;

        public UnityWebRequestTransport()
        {
            _mainThread = SynchronizationContext.Current;
        }

        public Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var completion = new TaskCompletionSource<StrybokHttpResponse>();
            RunOnMainThread(() => Start(request, cancellationToken, completion));
            return completion.Task;
        }

        private void Start(
            StrybokHttpRequest request,
            CancellationToken cancellationToken,
            TaskCompletionSource<StrybokHttpResponse> completion)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                return;
            }

            UnityWebRequest webRequest = null;
            try
            {
                webRequest = new UnityWebRequest(request.Url, request.Method);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                if (request.Body != null)
                {
                    webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                }
                if (request.Headers != null)
                {
                    foreach (KeyValuePair<string, string> header in request.Headers)
                    {
                        webRequest.SetRequestHeader(header.Key, header.Value);
                    }
                }
                // The SDK enforces the precise timeout; this is only a safety net
                // (UnityWebRequest counts whole seconds).
                webRequest.timeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds) + 1);

                UnityWebRequest running = webRequest;
                bool finished = false;
                CancellationTokenRegistration registration = cancellationToken.Register(() =>
                    RunOnMainThread(() =>
                    {
                        if (!finished) running.Abort();
                    }));

                UnityWebRequestAsyncOperation operation = running.SendWebRequest();
                operation.completed += _ =>
                {
                    finished = true;
                    try
                    {
                        if (running.result == UnityWebRequest.Result.ConnectionError ||
                            running.result == UnityWebRequest.Result.DataProcessingError ||
                            running.responseCode <= 0)
                        {
                            completion.TrySetException(new IOException(running.error ?? "Request failed"));
                        }
                        else
                        {
                            // ProtocolError means "HTTP status >= 400": still an answer.
                            string text = running.downloadHandler != null ? running.downloadHandler.text : null;
                            completion.TrySetResult(new StrybokHttpResponse((int)running.responseCode, text));
                        }
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                    finally
                    {
                        registration.Dispose();
                        running.Dispose();
                    }
                };
            }
            catch (Exception error)
            {
                if (webRequest != null) webRequest.Dispose();
                completion.TrySetException(error);
            }
        }

        private void RunOnMainThread(Action action)
        {
            if (_mainThread == null || SynchronizationContext.Current == _mainThread)
            {
                action();
            }
            else
            {
                _mainThread.Post(_ => action(), null);
            }
        }
    }
}
