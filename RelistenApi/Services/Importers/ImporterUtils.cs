using System;

namespace Relisten.Import;

public class ImporterUtils
{
    public ImporterUtils() { }

    public static int TryParseInt(string str)
    {
        return int.TryParse(str, out var i) ? i : 0;
    }

    public static double TryParseDouble(string str)
    {
        return double.TryParse(str, out var i) ? i : 0;
    }

    public static decimal TryParseDecimal(string str)
    {
        return decimal.TryParse(str, out var i) ? i : 0;
    }
}
