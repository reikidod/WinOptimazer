using System.Net.Http;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>HTTP-клиент синхронизации темы через бесплатный Cloudflare Worker.</summary>
public sealed class ThemeSyncService
{
    public const string ApiBaseUrl = "https://winoptimizer-theme-sync-api.aceberi86.workers.dev";
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(ApiBaseUrl + "/"),
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsConnected => !string.IsNullOrWhiteSpace(ThemeSyncCredentialStore.Read());

    public async Task<PairingSession> CreatePairingAsync(
        string deviceId, string themeId, string accentId, CancellationToken cancellationToken = default)
    {
        var pollSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var response = await Http.PostAsJsonAsync("v1/pairings", new
        {
            deviceId,
            pollSecret
        }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<PairingStartResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Cloudflare вернул пустой ответ при создании кода.");

        if (string.IsNullOrWhiteSpace(result.PairingId) || string.IsNullOrWhiteSpace(result.Code))
            throw new InvalidOperationException("Cloudflare вернул некорректный код привязки.");

        return new PairingSession(result.PairingId, pollSecret, result.Code, result.ExpiresIn);
    }

    public async Task<LinkedDevice?> PollPairingAsync(
        PairingSession session, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync("v1/pairings/poll", new
        {
            pairingId = session.PairingId,
            pollSecret = session.PollSecret
        }, JsonOptions, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Accepted)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<PairingPollResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Cloudflare вернул пустой ответ при проверке привязки.");

        if (!string.Equals(result.Status, "linked", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(result.DeviceToken))
            return null;

        // Сохраняем одноразово выданный токен сразу: Worker удаляет зашифрованный конверт после доставки.
        ThemeSyncCredentialStore.Write(result.DeviceToken);
        return new LinkedDevice(result.DeviceToken, result.Theme);
    }

    public async Task<ThemeSnapshot> GetThemeAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, "v1/theme", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ThemeSnapshot>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Cloudflare вернул пустую тему.");
    }

    public async Task<ThemeSnapshot> SetThemeAsync(
        string token, string themeId, string accentId, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Put, "v1/theme", token);
        request.Content = JsonContent.Create(new { id = themeId, accent = accentId }, options: JsonOptions);
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ThemeSnapshot>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Cloudflare вернул пустое подтверждение темы.");
    }

    public async Task RevokeDeviceAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, "v1/device/revoke", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public static string? ReadDeviceToken() => ThemeSyncCredentialStore.Read();
    public static void RemoveDeviceToken() => ThemeSyncCredentialStore.Delete();

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string details;
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, cancellationToken);
            details = payload?.Error ?? response.ReasonPhrase ?? "Неизвестная ошибка";
        }
        catch (JsonException)
        {
            details = response.ReasonPhrase ?? "Неизвестная ошибка";
        }

        throw new HttpRequestException(
            $"Cloudflare API: {(int)response.StatusCode} — {details}",
            inner: null,
            response.StatusCode);
    }

    public sealed record PairingSession(string PairingId, string PollSecret, string Code, int ExpiresIn);
    public sealed record LinkedDevice(string DeviceToken, ThemeSnapshot? Theme);

    public sealed class ThemeSnapshot
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("accent")] public string Accent { get; set; } = "theme";
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    }

    private sealed class PairingStartResponse
    {
        [JsonPropertyName("pairingId")] public string PairingId { get; set; } = "";
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
    }

    private sealed class PairingPollResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = "";
        [JsonPropertyName("theme")] public ThemeSnapshot? Theme { get; set; }
    }

    private sealed class ApiError
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}

/// <summary>
/// Токен API хранится в Windows Credential Manager (generic credential текущего пользователя),
/// а не в открытом settings.json или реестре.
/// </summary>
internal static class ThemeSyncCredentialStore
{
    private const string TargetName = "WinOptimizer.ThemeSync.DeviceToken";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 ||
            token.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Токен устройства имеет неверный формат.", nameof(token));

        var blob = Encoding.UTF8.GetBytes(token);
        var target = Marshal.StringToHGlobalUni(TargetName);
        var user = Marshal.StringToHGlobalUni("WinOptimizer");
        var data = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = PersistLocalMachine
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось сохранить токен в Windows Credential Manager.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(data);
        }
    }

    public static string? Read()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new Win32Exception(error, "Не удалось прочитать токен из Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize != 64)
                return null;

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                var token = Encoding.UTF8.GetString(blob);
                return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete()
    {
        if (CredDelete(TargetName, GenericCredential, 0))
            return;

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "Не удалось удалить токен из Windows Credential Manager.");
    }
}
