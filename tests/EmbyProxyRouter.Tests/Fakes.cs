using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// An <see cref="ILogger"/> that keeps what it was told, so a test can assert on log output.
    /// </summary>
    /// <remarks>
    /// The gate treats logging as part of its contract rather than as a side effect — "never
    /// silently" is the property the plugin exists to guarantee — so the warnings it does and does
    /// not write are worth asserting on directly.
    /// </remarks>
    internal sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = new List<string>();

        public IReadOnlyList<string> Warnings
        {
            get { return _warnings; }
        }

        public void Warn(string message, params object[] paramList)
        {
            _warnings.Add(Render(message, paramList));
        }

        public void Info(string message, params object[] paramList)
        {
        }

        public void Error(string message, params object[] paramList)
        {
        }

        public void Debug(string message, params object[] paramList)
        {
        }

        public void Fatal(string message, params object[] paramList)
        {
        }

        public void FatalException(string message, Exception exception, params object[] paramList)
        {
        }

        public void ErrorException(string message, Exception exception, params object[] paramList)
        {
        }

        public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent)
        {
        }

        public void Log(LogSeverity severity, string message, params object[] paramList)
        {
        }

        private static string Render(string message, object[] paramList)
        {
            if (paramList == null || paramList.Length == 0)
            {
                return message;
            }

            try
            {
                return string.Format(message, paramList);
            }
            catch (FormatException)
            {
                return message;
            }
        }

        // Emby marks the ReadOnlyMemory<char> overloads [Obsolete(error: true)], which makes even
        // implementing them a compile error. They still have to exist to satisfy the interface.
#pragma warning disable CS0619
        public void Log(LogSeverity severity, ReadOnlyMemory<char> message)
        {
        }

        public void Error(ReadOnlyMemory<char> message)
        {
        }

        public void Warn(ReadOnlyMemory<char> message)
        {
        }

        public void Info(ReadOnlyMemory<char> message)
        {
        }

        public void Debug(ReadOnlyMemory<char> message)
        {
        }
#pragma warning restore CS0619
    }

    /// <summary>
    /// Stands in for the real SocketsHttpHandler at the bottom of the gate's pipeline.
    /// </summary>
    /// <remarks>
    /// Records whether it was reached at all, which is the whole question for a fail-closed test: a
    /// blocked request must never arrive here, and one the gate allows must.
    /// </remarks>
    internal sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }
}
