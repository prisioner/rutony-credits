using System.Runtime.InteropServices;

namespace RutonyCredits;

internal static class SystemFonts
{
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? FaceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct EnumLogFontEx
    {
        public LogFont Font;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? FullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Style;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Script;
    }
    internal sealed record NativeFace(string LocalName, int Weight, bool Italic);
    internal sealed record LocalFace(string LocalName, int Weight);

    // GDI weights belong to each legacy family: a single Black/Thin face is still
    // that family's ordinary face. Never guess style from localized names.
    internal static LocalFace[] SelectFaces(IEnumerable<NativeFace> source)
    {
        var faces = source.Where(f => !f.Italic && ValidName(f.LocalName, 64))
            .OrderBy(f => Math.Abs((long)f.Weight - 400)).ThenBy(f => f.Weight)
            .ThenBy(f => f.LocalName, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(f => f.LocalName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (faces.Length == 0) return [];
        var ordinary = faces[0];
        var bold = faces.Where(f => f.Weight > ordinary.Weight)
            .OrderBy(f => Math.Abs((long)f.Weight - 700)).ThenBy(f => f.Weight).FirstOrDefault();
        return bold == null ? [new(ordinary.LocalName,400)]
            : [new(ordinary.LocalName,400),new(bold.LocalName,700)];
    }

    internal static bool ValidName(string? name, int maximum = 100) => name is { Length: > 0 }
        && name.Length <= maximum && !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@') && !name.Any(char.IsControl);

    public static LocalFace[] ReadFaces(string family)
    {
        if (!ValidName(family)) throw new ArgumentException(nameof(family));
        // An unknown LOGFONT face silently enumerates a substitute on some GDI
        // implementations. Require membership in the unfiltered family catalog.
        var known = Read().FirstOrDefault(n => StringComparer.OrdinalIgnoreCase.Equals(n,family));
        if (known == null || !OperatingSystem.IsWindows()) return [];
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return [];
        try
        {
            var faces = new List<NativeFace>();
            var filter = new LogFont { CharSet = 1, FaceName = known };
            FontEnum callback = (font, _, _, _) =>
            {
                var info = Marshal.PtrToStructure<EnumLogFontEx>(font);
                if (StringComparer.OrdinalIgnoreCase.Equals(info.Font.FaceName,known)
                    && info.FullName is {} name && ValidName(name,64))
                    faces.Add(new(name,info.Font.Weight,info.Font.Italic != 0));
                return 1;
            };
            EnumFontFamiliesEx(dc,ref filter,callback,IntPtr.Zero,0);
            GC.KeepAlive(callback);
            return SelectFaces(faces);
        }
        finally { DeleteDC(dc); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FontEnum(IntPtr font, IntPtr metrics, uint kind, IntPtr data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "EnumFontFamiliesExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr dc, ref LogFont filter, FontEnum callback, IntPtr data, uint flags);

    public static string[] Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return [];
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filter = new LogFont { CharSet = 1, FaceName = "" };
            FontEnum callback = (font, _, _, _) =>
            {
                var name = Marshal.PtrToStructure<LogFont>(font).FaceName?.Trim();
                if (name is { Length: > 0 and <= 100 } && !name.StartsWith('@') && !name.Any(char.IsControl))
                    names.Add(name);
                return 1;
            };
            EnumFontFamiliesEx(dc, ref filter, callback, IntPtr.Zero, 0);
            GC.KeepAlive(callback);
            return names.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { DeleteDC(dc); }
    }
}
