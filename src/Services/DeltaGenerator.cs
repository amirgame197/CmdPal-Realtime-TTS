using System.Globalization;
using System.Text;
using CmdPalRealtimeTTS.Configuration;

namespace CmdPalRealtimeTTS.Services;

public sealed class DeltaGenerator : IDisposable
{
    private static readonly TimeSpan InactivityDelay = TimeSpan.FromMilliseconds(1500);
    private readonly Lock _gate = new();
    private readonly Func<TtsConfig> _getConfig;
    private readonly Action<string> _enqueue;
    private readonly Action _abort;
    private readonly Timer _timer;
    private string _text = string.Empty;
    private int _generatedLength;

    public DeltaGenerator(Func<TtsConfig> getConfig, Action<string> enqueue, Action abort)
    {
        _getConfig = getConfig;
        _enqueue = enqueue;
        _abort = abort;
        _timer = new Timer(OnInactivity, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Update(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (string.Equals(_text, text, StringComparison.Ordinal))
            {
                return;
            }

            var wasBackspace = text.Length < _text.Length && _text.StartsWith(text, StringComparison.Ordinal);
            if (wasBackspace && _getConfig().AbortOnBackspace)
            {
                _abort();
            }

            if (!text.StartsWith(_text, StringComparison.Ordinal))
            {
                var commonLength = 0;
                while (commonLength < _text.Length &&
                       commonLength < text.Length &&
                       _text[commonLength] == text[commonLength])
                {
                    commonLength++;
                }

                _generatedLength = Math.Min(_generatedLength, commonLength);
            }

            _text = text;
            _generatedLength = Math.Min(_generatedLength, _text.Length);

            if (_generatedLength == _text.Length)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            var delta = _text[_generatedLength..];
            var config = _getConfig();
            var punctuationTriggered = config.DeltaOnPunctuation && HasPunctuation(delta);
            var wordsTriggered = config.DeltaOnThreeWords && Has3Words15Chars(delta);

            if (punctuationTriggered || wordsTriggered)
            {
                GeneratePending();
                return;
            }

            if (config.DeltaOnInactivity)
            {
                _timer.Change(InactivityDelay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public bool GeneratePending()
    {
        lock (_gate)
        {
            if (_generatedLength >= _text.Length)
            {
                return false;
            }

            var delta = _text[_generatedLength..].Trim();
            _generatedLength = _text.Length;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (delta.Length == 0)
            {
                return false;
            }

            _enqueue(delta);
            return true;
        }
    }

    private void OnInactivity(object? state)
    {
        lock (_gate)
        {
            if (_getConfig().DeltaOnInactivity)
            {
                GeneratePending();
            }
        }
    }

    private static bool HasPunctuation(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category
                is UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Has3Words15Chars(string value)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 && value.Count(character => !char.IsWhiteSpace(character)) >= 15;
    }

    public void Dispose() => _timer.Dispose();
}
