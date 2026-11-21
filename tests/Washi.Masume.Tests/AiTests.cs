// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Washi.Ai;
using Washi.Core.Forms;
using Washi.Masume;
using Washi.Tools;

namespace Washi.Masume.Tests;

/// <summary>A model that records what it was sent and answers from a function.</summary>
public sealed class FakeModel(Func<AiRequest, string> answer, bool offDevice = true, bool images = true) : IAiModel
{
    public List<AiRequest> Requests { get; } = [];
    public string Describe => "fake-model (test)";
    public bool SendsDataOffDevice => offDevice;
    public bool AcceptsImages => images;

    public Task<string> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}

public class SecretMaskerTests
{
    [Fact]
    public void Long_numbers_are_hidden_and_restored()
    {
        var m = new SecretMasker();
        var masked = m.Mask("マイナンバー: 1234 5678 9012\n口座: 1234567\n電話 090-1234-5678\n再掲 123456789012");
        Assert.DoesNotContain("5678 9012", masked);
        Assert.DoesNotContain("090-1234-5678", masked);
        Assert.Contains("1234567", masked);                       // 7 digits: kept
        Assert.Equal(2, m.Count);                                 // the same number twice -> one placeholder
        Assert.Contains("{{SECRET_1}}: a 12-digit number", m.Hints);
        Assert.Equal("123456789012", m.Unmask("{{SECRET_1}}", digitsOnly: true));
        Assert.Equal("1234 5678 9012", m.Unmask("{{SECRET_1}}"));
    }

    [Theory]
    [InlineData("生年月日 1999-06-27")]
    [InlineData("born 1999/6/27")]
    [InlineData("〒175-0094")]
    public void Dates_and_postal_codes_are_not_hidden(string text)
    {
        var m = new SecretMasker();
        Assert.Equal(text, m.Mask(text));
        Assert.Equal(0, m.Count);
    }
}

public sealed class AiFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-ai-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FlatForm()
    {
        var path = Path.Combine(_dir, "form.pdf");
        ToolsTests.MakeFlatForm(path);
        return path;
    }

    private static JsonElement Boxes(AiRequest r)
    {
        var json = r.Prompt[(r.Prompt.IndexOf("Boxes:\n", StringComparison.Ordinal) + 7)..];
        int end = json.IndexOf("\n\n", StringComparison.Ordinal);
        return JsonDocument.Parse(end > 0 ? json[..end] : json).RootElement;
    }

    [Fact]
    public async Task Naming_applies_the_models_names_and_kinds_and_ignores_made_up_ids()
    {
        var pdf = FlatForm();
        var fields = FormPipeline.Detect(pdf).Fields;
        var model = new FakeModel(r =>
        {
            var items = Boxes(r).EnumerateArray().Select(b =>
            {
                int id = b.GetProperty("id").GetInt32();
                var box = b.GetProperty("box").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                Assert.All(box, v => Assert.InRange(v, 0, 1000));
                var kind = b.GetProperty("kind").GetString();
                return new { id, name = kind == "comb" ? "個人番号" : "氏名", kind, max_len = kind == "comb" ? 12 : 0 };
            }).Append(new { id = 999, name = "invented", kind = "text", max_len = 0 });
            return JsonSerializer.Serialize(new { fields = items });
        });

        var r = await AiFeatures.NameFieldsAsync(model, pdf, fields);

        var request = Assert.Single(model.Requests);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, request.Images[0].Data[..4]);
        Assert.NotNull(request.Schema);
        Assert.Equal(fields.Count, r.Fields.Count);
        Assert.Contains(r.Fields, f => f.Name == "個人番号" && f.Kind == FieldKind.Comb && f.MaxLen == 12);
        Assert.DoesNotContain(r.Fields, f => f.Name == "invented");
        Assert.Equal(r.Fields.Count, r.Fields.Select(f => f.Name).Distinct().Count());   // 氏名, 氏名_2, ...
    }

    [Fact]
    public async Task Fill_plan_never_sends_long_numbers_and_puts_them_back_locally()
    {
        var flat = FlatForm();
        var pdf = Path.Combine(_dir, "fillable.pdf");
        AcroFormWriter.Write(flat, pdf, FormPipeline.Detect(flat).Fields.Select(f => f.ToFormField()).ToList(), FormScript.Japanese);
        var fields = AcroFormFields.List(pdf);
        var text = fields.First(f => f.Type == "text" && !f.Comb).Name;
        var comb = fields.First(f => f.Comb).Name;
        var box = fields.First(f => f.Type == "checkbox").Name;

        var model = new FakeModel(r =>
        {
            Assert.DoesNotContain("5678", r.Prompt);
            Assert.Contains("{{SECRET_1}}: a 12-digit number", r.Prompt);
            return JsonSerializer.Serialize(new
            {
                values = new[]
                {
                    new { name = text, value = "王 燁" },
                    new { name = comb, value = "{{SECRET_1}}" },
                    new { name = box, value = "true" },
                    new { name = "nope", value = "x" },
                },
                notes = new[] { "check the address" },
            });
        });

        var profile = "氏名: 王 燁\nマイナンバー: 1234 5678 9012\n";
        Assert.Equal(1, AiFeatures.DescribeFill(model, fields, profile).MaskedNumbers);
        var plan = await AiFeatures.PlanFillAsync(model, pdf, fields, profile);

        Assert.Equal("123456789012", plan.Values[comb]);
        Assert.Contains(comb, plan.SecretFields);
        Assert.DoesNotContain("nope", plan.Values.Keys);
        Assert.Contains(plan.Notes, n => n.Contains("nope"));

        var output = Path.Combine(_dir, "filled.pdf");
        AcroFormFields.Fill(pdf, output, plan.Values);
        var after = AcroFormFields.List(output).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("王 燁", after[text]);
        Assert.Equal("123456789012", after[comb]);
        Assert.Equal("Yes", after[box]);
    }

    [Fact]
    public async Task Asking_about_a_page_without_text_sends_the_page_image()
    {
        var pdf = FlatForm();
        var model = new FakeModel(_ => "  It is an empty test form.  ");
        Assert.Equal(1, AiFeatures.DescribeAsk(model, pdf).PageImages);

        var answer = await AiFeatures.AskAsync(model, pdf, "What is this?");

        Assert.Equal("It is an empty test form.", answer);
        var r = Assert.Single(model.Requests);
        Assert.Single(r.Images);
        Assert.EndsWith("Question: What is this?", r.Prompt);
        Assert.Null(r.Schema);
    }

    [Fact]
    public async Task Text_only_models_get_page_text_instead_of_images()
    {
        var pdf = FlatForm();
        var model = new FakeModel(_ => """{"fields":[]}""", images: false);
        await AiFeatures.NameFieldsAsync(model, pdf, FormPipeline.Detect(pdf).Fields);
        Assert.Empty(model.Requests[0].Images);
        Assert.Contains("page image is not available", model.Requests[0].Prompt);
    }

    [Fact]
    public async Task Invalid_json_from_the_model_is_a_clear_error()
    {
        var pdf = FlatForm();
        var model = new FakeModel(_ => "sorry, I cannot help");
        var e = await Assert.ThrowsAsync<AiException>(() => AiFeatures.NameFieldsAsync(model, pdf, FormPipeline.Detect(pdf).Fields));
        Assert.Contains("valid JSON", e.Message);
    }
}

public class AiSettingsTests
{
    [Fact]
    public void Settings_round_trip_without_secrets()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ai.json");
        new AiSettings(AiProvider.DeepSeek, "deepseek-chat").Save(path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new AiSettings(AiProvider.DeepSeek, "deepseek-chat"), AiSettings.Load(path));
        Assert.Equal(AiSettings.DefaultClaude, AiSettings.Load(path + ".missing"));
    }

    [Fact]
    public void Local_endpoints_keep_data_on_the_machine()
    {
        Assert.False(new AiSettings(AiProvider.OpenAICompatible, "llama3", new Uri("http://localhost:11434/v1")).SendsDataOffDevice);
        Assert.True(new AiSettings(AiProvider.OpenAICompatible, "x", new Uri("https://example.com/v1")).SendsDataOffDevice);
        Assert.True(AiSettings.DefaultClaude.SendsDataOffDevice);
    }

    [Fact]
    public void A_compatible_provider_needs_an_endpoint() =>
        Assert.Contains("endpoint", Assert.Throws<AiException>(() => new AiSettings(AiProvider.OpenAICompatible, "m").CreateModel()).Message);
}

public class ThinkingTests
{
    [Fact]
    public void Reasoning_preamble_from_local_models_is_removed() =>
        Assert.Equal("答え", Washi.Ai.OpenAICompatibleModel.StripThinking("<think>\nlet me see\n</think>\n\n答え"));
}
