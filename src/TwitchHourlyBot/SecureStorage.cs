using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace TwitchHourlyBot;

/// <summary>
/// Windows DPAPI wrapper. Secrets are protected for the current Windows user/machine.
/// No NuGet dependency is required.
/// </summary>
public static class SecureStorage
{
    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string value)
    {
        if (value is null) return "";

        var input = Encoding.UTF8.GetBytes(value);
        var inputPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inputBlob = new DataBlob { CbData = input.Length, PbData = inputPtr };
            if (!CryptProtectData(ref inputBlob, "TwitchHourlyBot", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var outputBlob))
                throw new SecurityException($"Windows DPAPI protect failed: {Marshal.GetLastWin32Error()}");

            try
            {
                var output = new byte[outputBlob.CbData];
                Marshal.Copy(outputBlob.PbData, output, 0, output.Length);
                return Convert.ToBase64String(output);
            }
            finally
            {
                LocalFree(outputBlob.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";

        byte[] input;
        try { input = Convert.FromBase64String(protectedValue); }
        catch { return ""; }

        var inputPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inputBlob = new DataBlob { CbData = input.Length, PbData = inputPtr };
            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var outputBlob))
                return "";

            try
            {
                var output = new byte[outputBlob.CbData];
                Marshal.Copy(outputBlob.PbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                LocalFree(outputBlob.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static void SaveSecret(string fileName, string value)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), Protect(value));
    }

    public static string LoadSecret(string fileName)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
            var path = Path.Combine(folder, fileName);
            return File.Exists(path) ? Unprotect(File.ReadAllText(path)) : "";
        }
        catch { return ""; }
    }

    public static void SaveProtectedJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, Protect(json));
    }

    public static T? LoadProtectedJson<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var json = Unprotect(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return default;
        }
    }
}
