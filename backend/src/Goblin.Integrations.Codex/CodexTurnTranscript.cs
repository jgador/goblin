using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

// Owns app-server turn identity, notification correlation and bounded transcript
// collection. Callers retain endpoint-specific completion and failure policy.
internal sealed class CodexTurnTranscript
{
    private readonly object _gate = new();
    private readonly int _maxMessages;
    private readonly int _maxCharacters;
    private readonly string _separator;
    private readonly bool _countSeparators;
    private readonly bool _trimResult;
    private readonly int _progressLimit;
    private readonly Func<Exception> _tooLarge;
    private readonly Func<Turn, Exception?> _completionError;
    private readonly Func<string, Exception?> _resultError;
    private readonly Func<Exception>? _notificationError;
    private readonly OrderedDictionary<string, string> _messages = new();
    private string? _threadId;
    private string? _turnId;
    private string? _latestProgress;
    private bool _finished;

    internal CodexTurnTranscript(int maxMessages, int maxCharacters, string separator,
        bool countSeparators, bool trimResult, int progressLimit, Func<Exception> tooLarge,
        Func<Turn, Exception?> completionError, Func<string, Exception?> resultError,
        Func<Exception>? notificationError = null)
    {
        _maxMessages = maxMessages;
        _maxCharacters = maxCharacters;
        _separator = separator;
        _countSeparators = countSeparators;
        _trimResult = trimResult;
        _progressLimit = progressLimit;
        _tooLarge = tooLarge;
        _completionError = completionError;
        _resultError = resultError;
        _notificationError = notificationError;
    }

    internal TaskCompletionSource<string> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string? ThreadId { get { lock (_gate) return _threadId; } }

    internal string? TurnId { get { lock (_gate) return _turnId; } }

    internal string? LatestProgress { get { lock (_gate) return _latestProgress; } }

    internal bool Finished { get { lock (_gate) return _finished; } }

    internal void SetThread(string threadId)
    {
        lock (_gate) _threadId = threadId;
    }

    internal void SetTurn(string turnId)
    {
        lock (_gate)
        {
            if (_turnId is not null && _turnId != turnId) throw IntegrationFailure.RuntimeUnavailable();
            _turnId = turnId;
        }
    }

    internal void Fail(Exception error) => Completion.TrySetException(error);

    internal void Handle(ServerNotification notification)
    {
        lock (_gate)
        {
            (string? threadId, string? turnId) = notification switch
            {
                TurnStartedServerNotification value => (value.Params.ThreadId, value.Params.Turn.Id),
                ItemCompletedServerNotification value => (value.Params.ThreadId, value.Params.TurnId),
                TurnCompletedServerNotification value => (value.Params.ThreadId, value.Params.Turn.Id),
                ErrorServerNotification value when _notificationError is not null => (value.Params.ThreadId, value.Params.TurnId),
                _ => (null, null)
            };
            if (_threadId is null || _threadId != threadId || turnId is null ||
                (_turnId is not null && _turnId != turnId)) return;
            _turnId ??= turnId;
            if (notification is ErrorServerNotification)
            {
                Completion.TrySetException(_notificationError!());
                return;
            }
            if (notification is ItemCompletedServerNotification item) Save(item.Params.Item);
            if (notification is not TurnCompletedServerNotification completed) return;
            _finished = true;
            Turn turn = completed.Params.Turn;
            Exception? completionError = _completionError(turn);
            if (completionError is not null)
            {
                Completion.TrySetException(completionError);
                return;
            }
            foreach (ThreadItem turnItem in turn.Items) Save(turnItem);
            string result = string.Join(_separator, _messages.Values);
            if (_trimResult) result = result.Trim();
            Exception? resultError = _resultError(result);
            if (resultError is not null) Completion.TrySetException(resultError);
            else Completion.TrySetResult(result);
        }
    }

    private void Save(ThreadItem item)
    {
        if (item is not AgentMessageThreadItem message) return;
        if (message.Phase == MessagePhase.Commentary)
        {
            if (_progressLimit > 0)
                _latestProgress = message.Text[..Math.Min(message.Text.Length, _progressLimit)];
            return;
        }
        _messages[message.Id] = message.Text;
        int length = _countSeparators
            ? string.Join(_separator, _messages.Values).Length
            : TotalMessageLength();
        if (_messages.Count > _maxMessages || length > _maxCharacters)
            Completion.TrySetException(_tooLarge());
    }

    private int TotalMessageLength()
    {
        int length = 0;
        foreach (string message in _messages.Values) length += message.Length;
        return length;
    }
}
