using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RutonyCredits;

// Non-secret settings and a current-Windows-user DPAPI password are separate.
// No password (including a previously saved one) is returned by the HTTP model.
internal sealed class ObsSettingsStore(string directory)
{
    private string OptionsPath => Path.Combine(directory, "obs.json");
    private string PasswordPath => Path.Combine(directory, "obs-secret.bin");
    public (ObsOptions Options, string? Password) Load()
    {
        var options = File.Exists(OptionsPath) ? JsonSerializer.Deserialize<ObsOptions>(BoundedFiles.Read(OptionsPath, BoundedFiles.ObsBytes), Wire.Json) ?? throw new InvalidDataException() : new();
        ObsController.Validate(options);
        return (options, ReadPassword());
    }
    private string? ReadPassword()
    {
        var cipher = File.Exists(PasswordPath) ? BoundedFiles.Read(PasswordPath, BoundedFiles.ObsBytes) : [];
        if (cipher.Length == 0) return null;
        var plain = Transform(cipher, encrypt: false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string? Save(ObsOptions options, string? password, bool changePassword)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(options, Wire.Json);
        byte[]? cipher = null;
        if (changePassword)
        {
            var plain = Encoding.UTF8.GetBytes(password ?? "");
            try { cipher = plain.Length == 0 ? [] : Transform(plain, encrypt: true); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        Directory.CreateDirectory(directory);
        // Preserving a password is only meaningful when it can still be read.
        // An explicit replacement/clear is allowed even for oversized ciphertext.
        var retainedPassword = changePassword ? password : ReadPassword();
        var previous = changePassword ? BoundedFiles.Preserve(PasswordPath) : null;
        try
        {
            if (File.Exists(OptionsPath))
            {
                try
                {
                    var old = JsonSerializer.Deserialize<ObsOptions>(BoundedFiles.Read(OptionsPath, BoundedFiles.ObsBytes), Wire.Json) ?? throw new InvalidDataException();
                    ObsController.Validate(old);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
                { BoundedFiles.Preserve(OptionsPath); }
            }
            if (cipher != null) AtomicWrite(PasswordPath, cipher);
            try { AtomicWrite(OptionsPath, json); }
            catch
            {
                // Roll back from the byte-for-byte backup without allocating its size.
                if (cipher != null)
                {
                    if (previous != null) RestoreBackup(previous, PasswordPath);
                    else File.Delete(PasswordPath);
                }
                throw;
            }
        }
        finally { if (cipher != null) CryptographicOperations.ZeroMemory(cipher); }
        return retainedPassword;
    }
    private static void RestoreBackup(string backup, string target)
    {
        var temporary = target + ".rollback-" + Guid.NewGuid().ToString("N");
        try { File.Copy(backup, temporary); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void AtomicWrite(string target, byte[] data)
    {
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Пароль OBS хранится с помощью Windows DPAPI.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "RutonyCredits OBS", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Не удалось обработать пароль OBS в Windows DPAPI.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                // The unprotected output is plaintext; erase it before LocalFree.
                if (!encrypt) for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
