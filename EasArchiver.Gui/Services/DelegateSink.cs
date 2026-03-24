using Serilog.Core;
using Serilog.Events;

namespace EasArchiver.Gui.Services;

/// <summary>Serilog sink that forwards formatted messages to a delegate.</summary>
public class DelegateSink(Action<string> write) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += Environment.NewLine + logEvent.Exception;
        write(message);
    }
}
