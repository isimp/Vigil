using BepInEx.Logging;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Listens to every BepInEx log line from the patcher's start on and keeps, with its time, each line
    /// that marks a startup step (<see cref="StartupLines.Classify"/>). BepInEx's preloader removes only
    /// its own listener when it ends and the chainloader only adds its own, so this one keeps hearing
    /// the chainloader's "Loading [...]" lines.
    /// </summary>
    internal sealed class StartupListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs == null) return;
            string message = eventArgs.Data?.ToString();
            string kind = StartupLines.Classify(eventArgs.Source?.SourceName, message);
            if (kind != null) StartupState.Add(kind, message);
        }

        public void Dispose()
        {
        }
    }
}
