using BepInEx.Logging;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Listens to every BepInEx log line from the patcher's start on and keeps it with its time: a line
    /// that marks a startup step as that step (<see cref="StartupLines.Classify"/>), any other as a plain
    /// log line, so the report can name the long quiet stretches by the lines either side. BepInEx's
    /// preloader removes only its own listener when it ends and the chainloader only adds its own, so
    /// this one keeps hearing the chainloader's "Loading [...]" lines and the game's own lines.
    /// </summary>
    internal sealed class StartupListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs == null) return;
            string message = eventArgs.Data?.ToString();
            string source = eventArgs.Source?.SourceName;
            string kind = StartupLines.Classify(source, message);
            if (kind != null) StartupState.Add(kind, message);
            else StartupState.AddLog(StartupLines.LogText(source, message));
        }

        public void Dispose()
        {
        }
    }
}
