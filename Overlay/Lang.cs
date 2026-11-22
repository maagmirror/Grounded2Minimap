using System.Globalization;

// UI language: English or Spanish. "auto" follows the Windows display language.
static class L
{
    public static bool Es { get; private set; }

    public static void Init(string pref)
    {
        Es = pref == "es" || (pref != "en" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es");
    }

    public static string T(string en, string es) => Es ? es : en;
}
