using System.Text;

namespace AXamlIDE.Services.Terminal;

/// <summary>
/// Removes terminal escape codes (colours, cursor moves, window titles) from program output, so the Terminal panel
/// shows plain text. Output arrives in chunks and a code can be split between two of them, so this remembers where
/// it was: feed every chunk through the same instance, in order.
///
/// The codes it knows: CSI (ESC [ ... a letter, e.g. "ESC[31m" for red), OSC (ESC ] ... ending in BEL or ESC \,
/// e.g. a window title), and the two-character ones (ESC and one more character).
/// </summary>
public sealed class AnsiStripper
{
    private const char Escape = '\u001B';
    private const char Bell = '\u0007';

    private enum State { Text, AfterEscape, Csi, Osc, OscAfterEscape }

    private State _state = State.Text;

    public string Strip(string chunk)
    {
        var text = new StringBuilder(chunk.Length);
        foreach (var c in chunk)
        {
            switch (_state)
            {
                case State.Text:
                    if (c == Escape)
                        _state = State.AfterEscape;
                    else if (c == Bell)
                        break;   // a beep: nothing to show
                    else
                        text.Append(c);
                    break;

                case State.AfterEscape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' => State.Osc,
                        _ => State.Text   // a two-character code: this character ends it
                    };
                    break;

                case State.Csi:
                    // Parameters and middle characters are 0x20-0x3F; the final character (0x40-0x7E) ends it.
                    if (c is >= '@' and <= '~')
                        _state = State.Text;
                    break;

                case State.Osc:
                    if (c == Bell)
                        _state = State.Text;
                    else if (c == Escape)
                        _state = State.OscAfterEscape;
                    break;

                case State.OscAfterEscape:
                    // ESC \ ends it; anything else, keep looking.
                    _state = c == '\\' ? State.Text : State.Osc;
                    break;
            }
        }

        return text.ToString();
    }
}
