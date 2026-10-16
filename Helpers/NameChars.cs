using System;
using AvaloniaEdit.Document;

namespace AXamlIDE.Helpers;
          /// <summary>
          /// Name-character helpers used by completion / typing.
          /// In MainWindow add:  using static AXamlIDE.Helpers.NameChars;
          /// </summary>
          public static class NameChars
          {
                    public static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

                    // Characters that are part of an AXAML name (Grid.Row, x:Name, local-thing).
                    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-';

                    public static int WordStart(TextDocument doc, int offset)
                    {
                              while (offset > 0 && IsNameChar(doc.GetCharAt(offset - 1))) offset--;
                              return offset;
                    }
          }
