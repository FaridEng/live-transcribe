namespace LiveTranscribe.Core.Diagnostics;

/// <summary>Logs never contain audio, transcripts or keys unless debug logging is turned on (FR-11.5).</summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
