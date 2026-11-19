using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Relay;

namespace PCL.Core.Test;

[TestClass]
public class RelayRealNameApiTest
{
    [TestMethod]
    public void IdentityNumberValidationRequiresValidStructureDateAndChecksum()
    {
        using var valid = ToSecureString("110105200001010016");
        using var wrongChecksum = ToSecureString("110105200001010017");
        using var legacy15Digit = ToSecureString("110105491231002");
        using var validX = ToSecureString("11010519491231002X");
        using var lowercaseX = ToSecureString("11010519491231002x");
        using var leapDay = ToSecureString("110105200002290013");

        Assert.IsTrue(RelayApi.IsValidIdentityNumber(valid));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(wrongChecksum));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(legacy15Digit));
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(validX));
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(lowercaseX));
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(leapDay));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(validX, new RelayRealNameRules(AllowChecksumX: false)));
    }

    [DataTestMethod]
    [DataRow("110105200102290010")] // Checksum is valid; the date does not exist.
    [DataRow("110105200013010013")]
    [DataRow("110105209901010012")]
    [DataRow("110105189912310015")]
    [DataRow("110105200001010008")] // Sequence 000 is invalid.
    [DataRow("000000200001010013")]
    [DataRow("11010520000101001Ａ")]
    public void IdentityNumberRejectsInvalidDateRangeAndReservedStructure(string value)
    {
        using var identity = ToSecureString(value);
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(identity));
    }

    [TestMethod]
    public void IdentityNumberUsesExactCloudBirthDateRange()
    {
        using var identity = ToSecureString("110105200001010016");
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(identity, new RelayRealNameRules(
            BirthDateMin: "2000-01-01", BirthDateMax: "2000-01-01")));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(identity, new RelayRealNameRules(BirthDateMin: "2000-01-02")));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(identity, new RelayRealNameRules(BirthDateMax: "1999-12-31")));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(identity, new RelayRealNameRules(BirthDateMin: "2000-2-1")));
        Assert.IsFalse(RelayApi.IsValidIdentityNumber(identity, new RelayRealNameRules(BirthDateMax: "9999-12-31")));
        Assert.IsFalse(new RelayRealNameRules(NameMaxLength: 7).IsValidConfiguration);
        Assert.IsFalse(new RelayRealNameRules(BirthDateMin: "2000-01-01", BirthDateMax: "1999-12-31").IsValidConfiguration);
    }

    [DataTestMethod]
    [DataRow("2008-11-19", "2026-11-18", 18, false)]
    [DataRow("2008-11-19", "2026-11-19", 18, true)]
    [DataRow("2008-11-20", "2026-11-19", 18, false)]
    [DataRow("2008-11-18", "2026-11-19", 18, true)]
    [DataRow("2008-02-29", "2026-02-28", 18, false)]
    [DataRow("2008-02-29", "2026-03-01", 18, true)]
    [DataRow("2008-02-29", "2024-02-29", 16, true)]
    [DataRow("2008-02-29", "2024-02-28", 16, false)]
    [DataRow("2026-11-19", "2026-11-19", 0, true)]
    public void MinimumAgeUsesExactAnniversaryAndTheCloudValidationDate(
        string birthday, string validationDate, int minimumAge, bool expected)
    {
        using var identity = ToSecureString(IdentityForBirthday(birthday));
        var rules = new RelayRealNameRules(BirthDateMax: validationDate, MinimumAge: minimumAge, ValidationDate: validationDate);
        Assert.IsTrue(rules.IsValidConfiguration);
        Assert.AreEqual(expected, RelayApi.IsRealNameMinimumAgeSatisfied(identity, rules));
        Assert.AreEqual(expected, RelayApi.IsValidIdentityNumber(identity, rules));
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(identity, rules with { MinimumAge = 0 }));
    }

    [TestMethod]
    public void MinimumAgeDoesNotTreatTheConfiguredBirthDateMaximumAsToday()
    {
        using var identity = ToSecureString(IdentityForBirthday("2008-11-19"));
        var rules = new RelayRealNameRules(BirthDateMax: "2020-01-01", MinimumAge: 18, ValidationDate: "2026-11-19");
        Assert.IsTrue(RelayApi.IsValidIdentityNumber(identity, rules));
        Assert.IsTrue(RelayApi.IsRealNameMinimumAgeSatisfied(identity, rules));
        Assert.IsFalse((rules with { MinimumAge = -1 }).IsValidConfiguration);
        Assert.IsFalse((rules with { MinimumAge = 151 }).IsValidConfiguration);
        Assert.IsFalse((rules with { ValidationDate = "2026-2-1" }).IsValidConfiguration);
        Assert.IsFalse((rules with { ValidationDate = null }).IsValidConfiguration);
        Assert.IsFalse((rules with { ValidationDate = "2019-01-01" }).IsValidConfiguration);
    }

    [TestMethod]
    public void NameInputAllowsOnlyTwoToSixChineseCharacters()
    {
        Assert.IsTrue(RelayApi.IsValidRealName("张三"));
        Assert.IsTrue(RelayApi.IsValidRealName("张张张张张张"));
        Assert.IsTrue(RelayApi.IsValidRealName("\U00020000张"));
        Assert.IsFalse(RelayApi.IsValidRealName("张"));
        Assert.IsFalse(RelayApi.IsValidRealName("张张张张张张张"));
        Assert.IsFalse(RelayApi.IsValidRealName("张 三"));
        Assert.IsFalse(RelayApi.IsValidRealName("张3"));
        Assert.IsFalse(RelayApi.IsValidRealName("张A"));
        Assert.IsFalse(RelayApi.IsValidRealName("张·三"));
        Assert.IsTrue(RelayApi.IsAllowedRealNameInput(string.Empty));
        Assert.IsFalse(RelayApi.IsAllowedRealNameInput("张\uD800"));
        Assert.IsFalse(RelayApi.IsAllowedRealNameInput("张\U0002A6E0"));
    }

    [TestMethod]
    public void IdentityInputAllowsXOnlyAtTheEighteenthPosition()
    {
        Assert.IsTrue(RelayApi.IsAllowedIdentityInput(string.Empty));
        Assert.IsTrue(RelayApi.IsAllowedIdentityInput("11010519491231002"));
        Assert.IsTrue(RelayApi.IsAllowedIdentityInput("11010519491231002X"));
        Assert.IsTrue(RelayApi.IsAllowedIdentityInput("11010519491231002x"));
        Assert.IsFalse(RelayApi.IsAllowedIdentityInput("X"));
        Assert.IsFalse(RelayApi.IsAllowedIdentityInput("1101051949123100X2"));
        Assert.IsFalse(RelayApi.IsAllowedIdentityInput("11010519491231002X0"));
        Assert.IsFalse(RelayApi.IsAllowedIdentityInput("１1010519491231002X"));
        Assert.IsFalse(RelayApi.IsAllowedIdentityInput("11010519491231002 "));
    }

    [TestMethod]
    public async Task StartRealNameUsesHybridEnvelopeAndRecordsTheAgreedTermsVersion()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var expectedName = new string('张', 6);
        const string expectedId = "110105200001010016";
        string? receivedName = null;
        string? receivedId = null;
        var requestCount = 0;

        using var http = new HttpClient(new FixtureHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith(
                "/api/v1/relay/real-name/start", StringComparison.Ordinal));
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("Bearer session-token", request.Headers.Authorization?.ToString());

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var envelope = JsonDocument.Parse(body);
            var root = envelope.RootElement;
            Assert.AreEqual(9, root.EnumerateObject().Count());
            Assert.IsFalse(root.TryGetProperty("encrypted_payload", out _));
            Assert.IsTrue(root.GetProperty("consented").GetBoolean());
            Assert.AreEqual("2026-11-19.1", root.GetProperty("terms_version").GetString());
            Assert.AreEqual("read-token", root.GetProperty("terms_read_token").GetString());
            Assert.IsTrue(root.GetProperty("terms_read_confirmed").GetBoolean());
            Assert.IsTrue(root.GetProperty("sensitive_data_consented").GetBoolean());
            Assert.IsTrue(root.GetProperty("alipay_data_consented").GetBoolean());
            Assert.IsFalse(body.Contains(expectedId, StringComparison.Ordinal));
            Assert.IsFalse(root.TryGetProperty("name", out _));

            var encryptedKey = Convert.FromBase64String(root.GetProperty("encrypted_key").GetString()!);
            var nonce = Convert.FromBase64String(root.GetProperty("nonce").GetString()!);
            var ciphertextAndTag = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);
            Assert.AreEqual(12, nonce.Length);
            Assert.IsTrue(ciphertextAndTag.Length > 16);

            var aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
            var plaintext = new byte[ciphertextAndTag.Length - 16];
            try
            {
                using var aes = new AesGcm(aesKey, 16);
                aes.Decrypt(nonce, ciphertextAndTag.AsSpan(0, plaintext.Length),
                    ciphertextAndTag.AsSpan(plaintext.Length, 16), plaintext);
                using var payload = JsonDocument.Parse(plaintext);
                receivedName = payload.RootElement.GetProperty("name").GetString();
                receivedId = payload.RootElement.GetProperty("identity_number").GetString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encryptedKey);
                CryptographicOperations.ZeroMemory(ciphertextAndTag);
                CryptographicOperations.ZeroMemory(nonce);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"pending\",\"qr_png\":\"aGVsbG8=\",\"masked_id\":\"**************5678\"}",
                    Encoding.UTF8, "application/json")
            };
        }));

        var api = new RelayApi(http, "3.2.2") { Token = "session-token" };
        using var identity = new SecureString();
        foreach (var character in expectedId) identity.AppendChar(character);
        identity.MakeReadOnly();

        var result = await api.StartRealNameAsync(publicKey, expectedName, identity, "2026-11-19.1", "read-token", true, true);

        Assert.AreEqual(1, requestCount);
        Assert.AreEqual(expectedName, receivedName);
        Assert.AreEqual(expectedId, receivedId);
        Assert.AreEqual("pending", result.Status);
        Assert.AreEqual("aGVsbG8=", result.QrPng);
        Assert.AreEqual("**************5678", result.MaskedId);
    }

    [TestMethod]
    public void StartRealNameRequiresAnExplicitTermsVersionBeforeSendingAnyData()
    {
        var requestCount = 0;
        using var http = new HttpClient(new FixtureHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var api = new RelayApi(http, "3.2.2");
        using var identity = ToSecureString("110105200001010016");

        Assert.ThrowsExactly<ArgumentException>(() => api.StartRealNameAsync("unused", "张三", identity, " ", "read-token", true, true));
        Assert.ThrowsExactly<ArgumentException>(() => api.StartRealNameAsync("unused", "Invalid", identity, "version-1", "read-token", true, true));
        Assert.ThrowsExactly<ArgumentException>(() => api.StartRealNameAsync("unused", "张三", identity, "version-1", " ", true, true));
        Assert.ThrowsExactly<ArgumentException>(() => api.StartRealNameAsync("unused", "张三", identity, "version-1", "read-token", false, true));
        Assert.ThrowsExactly<ArgumentException>(() => api.StartRealNameAsync("unused", "张三", identity, "version-1", "read-token", true, false));
        Assert.AreEqual(0, requestCount);
    }

    [TestMethod]
    public async Task OpeningTermsUsesTheAuthenticatedVersionAndReturnsAReadingReceipt()
    {
        var requestCount = 0;
        using var http = new HttpClient(new FixtureHandler(async (request, ct) =>
        {
            requestCount++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("Bearer session-token", request.Headers.Authorization?.ToString());
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/api/v1/relay/real-name/terms/read", StringComparison.Ordinal));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual(1, body.RootElement.EnumerateObject().Count());
            Assert.AreEqual("version-3", body.RootElement.GetProperty("terms_version").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"terms":{"title":"服务条款","version":"version-3","content":"条款正文"},"read_token":"read-token","minimum_read_seconds":10,"expires_at":"2026-11-19T12:30:00Z"}""", Encoding.UTF8, "application/json")
            };
        }));
        var api = new RelayApi(http, "3.2.2") { Token = "session-token" };
        Assert.ThrowsExactly<ArgumentException>(() => api.OpenRealNameTermsAsync(" "));
        var receipt = await api.OpenRealNameTermsAsync("version-3");
        Assert.AreEqual(1, requestCount);
        Assert.AreEqual("version-3", receipt.Terms.Version);
        Assert.AreEqual("条款正文", receipt.Terms.Content);
        Assert.AreEqual("read-token", receipt.ReadToken);
        Assert.AreEqual(10, receipt.MinimumReadSeconds);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 19, 12, 30, 0, TimeSpan.Zero), receipt.ExpiresAt);
    }

    [TestMethod]
    public async Task CloudStatusLoadsValidationRulesAndTheFullServiceTerms()
    {
        using var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "enabled":true,
                  "captcha":"math",
                  "real_name_enabled":true,
                  "real_name_rules": {
                    "name_min_length":2,"name_max_length":6,"identity_number_length":18,
                    "allow_checksum_x":true,"birth_date_min":"1900-01-01","birth_date_max":"2026-11-19",
                    "minimum_age":18,"validation_date":"2026-11-19"
                  },
                  "relay_terms":{"title":"中继联机服务条款","version":"version-2","content":"本人同意提供资料用于中继及支付宝实名认证。\n相关资料将加密处理。"}
                }
                """, Encoding.UTF8, "application/json")
        })));

        var status = await new RelayApi(http, "3.2.2").GetStatusAsync();
        Assert.IsTrue(status.RealNameEnabled);
        Assert.IsNotNull(status.RealNameRules);
        Assert.IsTrue(status.RealNameRules.IsValidConfiguration);
        Assert.AreEqual("1900-01-01", status.RealNameRules.BirthDateMin);
        Assert.AreEqual("2026-11-19", status.RealNameRules.BirthDateMax);
        Assert.AreEqual(18, status.RealNameRules.MinimumAge);
        Assert.AreEqual("2026-11-19", status.RealNameRules.ValidationDate);
        Assert.IsNotNull(status.RelayTerms);
        Assert.AreEqual("version-2", status.RelayTerms.Version);
        Assert.IsTrue(status.RelayTerms.Content.Contains('\n'));

        var legacy = JsonSerializer.Deserialize<RelayStatus>("{\"enabled\":true,\"captcha\":\"math\"}");
        Assert.IsNull(legacy!.RealNameRules);
        Assert.IsNull(legacy.RelayTerms);
    }

    [TestMethod]
    public async Task ClientStatusModelsExposeOnlyMaskedPersonalInformation()
    {
        using var http = new HttpClient(new FixtureHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? """{"user":{"id":"user-1","email":"user@example.test","phone":"138****1234","phone_bound":true,"real_name_verified":true,"real_name_id_masked":"110105********0016","identity_number":"server-only-field"},"quota":{},"allocations":[]}"""
                : """{"status":"verified","masked_id":"110105********0016","identity_number":"server-only-field","phone":"server-only-field"}""",
                Encoding.UTF8, "application/json")
        })));
        var api = new RelayApi(http, "3.2.2");

        var me = await api.GetMeAsync();
        var state = await api.GetRealNameStatusAsync();
        Assert.AreEqual("138****1234", me.User.Phone);
        Assert.AreEqual("110105********0016", me.User.RealNameIdMasked);
        Assert.AreEqual("110105********0016", state.MaskedId);
        Assert.IsFalse(JsonSerializer.Serialize(me.User).Contains("server-only-field", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(state).Contains("server-only-field", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdatedTermsErrorIsPresentedAsAnAuthorizationPrompt()
    {
        using var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"relay_terms_changed\"}", Encoding.UTF8, "application/json")
        })));
        var error = await Assert.ThrowsExactlyAsync<RelayApiException>(() => new RelayApi(http, "3.2.2").GetRealNameStatusAsync());
        Assert.AreEqual("中继服务条款已更新，请重新阅读并确认同意后提交。", error.Message);
    }

    [DataTestMethod]
    [DataRow("relay_terms_read_required", "请重新打开并完整阅读中继服务条款，确认后再提交实名认证。")]
    [DataRow("relay_terms_read_too_short", "条款阅读时间尚未达到要求，请在倒计时结束后确认。")]
    [DataRow("real_name_sensitive_consent_required", "请单独确认实名敏感个人信息处理授权。")]
    [DataRow("real_name_alipay_consent_required", "请单独确认向支付宝认证服务提供方提供实名资料的授权。")]
    [DataRow("identity_age_below_minimum", "根据当前服务规则，您尚未达到实名认证最低年龄要求。")]
    public async Task ConsentReadingAndAgeErrorsHaveFormalActionableMessages(string code, string expected)
    {
        using var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = code }), Encoding.UTF8, "application/json")
        })));
        var error = await Assert.ThrowsExactlyAsync<RelayApiException>(() => new RelayApi(http, "3.2.2").GetRealNameStatusAsync());
        Assert.AreEqual(expected, error.Message);
    }

    private static string IdentityForBirthday(string birthday)
    {
        var prefix = "110105" + birthday.Replace("-", string.Empty, StringComparison.Ordinal) + "001";
        int[] weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        var sum = prefix.Select((digit, index) => (digit - '0') * weights[index]).Sum();
        return prefix + "10X98765432"[sum % 11];
    }

    private static SecureString ToSecureString(string value)
    {
        var result = new SecureString();
        foreach (var character in value) result.AppendChar(character);
        result.MakeReadOnly();
        return result;
    }
}
