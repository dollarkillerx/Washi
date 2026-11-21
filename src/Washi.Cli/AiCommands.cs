// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Ai;
using Washi.Core.Fonts;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Masume;
using Washi.Tools;

/// <summary>washi ai …: the built-in AI from the command line.</summary>
internal static class AiCommands
{
    public static async Task<int> Run(string[] args)
    {
        var (pos, opt) = Parse(args);
        try
        {
            return pos switch
            {
                ["config"] => ShowConfig(),
                ["config", var provider, var model] => SaveConfig(provider, model, opt.GetValueOrDefault("--endpoint"), opt.GetValueOrDefault("--vision")),
                ["models"] => await Models(),
                ["key", var provider] => StoreKey(provider),
                ["name-fields", var pdf] => await NameFields(pdf, opt),
                ["fill", var pdf] => await Fill(pdf, opt),
                ["ask", var pdf, .. var words] when words.Length > 0 => await Ask(pdf, string.Join(' ', words), opt),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is AiException or HttpRequestException or System.ClientModel.ClientResultException
                                      or Anthropic.Exceptions.AnthropicException or OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine(L.F("Ai_Error", e.Message));
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(L.Get("UsageAi"));
        return 2;
    }

    // ------------------------------------------------------------------ settings
    private static int ShowConfig()
    {
        var s = AiSettings.Load();
        var key = s.Provider == AiProvider.OpenAICompatible ? "-" : ApiKeys.Find(s.Provider) is null ? L.Get("Ai_KeyMissing") : L.Get("Ai_KeyFound");
        Console.WriteLine(L.F("Ai_Config", $"{s.Model} ({s.Provider}{(s.Endpoint is { } e ? ", " + e : "")}, images {(s.AcceptsImages ? "on" : "off")})", key, AiSettings.ConfigPath));
        return 0;
    }

    private static async Task<int> Models()
    {
        foreach (var id in await AiSettings.Load().ListModelsAsync()) Console.WriteLine(id);
        return 0;
    }

    private static int SaveConfig(string provider, string model, string? endpoint, string? vision)
    {
        var p = ProviderOf(provider);
        bool? v = vision?.ToLowerInvariant() switch { "yes" or "true" or "on" => true, "no" or "false" or "off" => false, _ => null };
        var s = new AiSettings(p, model, endpoint is { Length: > 0 } ? new Uri(endpoint) : null, v);
        s.Save();
        Console.WriteLine(L.F("Ai_Saved", AiSettings.ConfigPath));
        return ShowConfig();
    }

    private static int StoreKey(string provider)
    {
        var p = ProviderOf(provider);
        Console.Error.Write(L.F("Ai_KeyPrompt", p));
        var key = ReadHidden().Trim();
        if (key.Length == 0) return 1;
        ApiKeys.Store(p, key);
        Console.WriteLine(L.F("Ai_KeyStored", p.ToString().ToLowerInvariant()));
        return 0;
    }

    private static AiProvider ProviderOf(string s) => s.ToLowerInvariant() switch
    {
        "anthropic" or "claude" => AiProvider.Anthropic,
        "openai" or "gpt" => AiProvider.OpenAI,
        "deepseek" => AiProvider.DeepSeek,
        "local" or "ollama" or "openai-compatible" => AiProvider.OpenAICompatible,
        _ => throw new AiException($"unknown provider '{s}' (anthropic, openai, deepseek, local)"),
    };

    // ------------------------------------------------------------------ features
    private static async Task<int> NameFields(string pdf, Dictionary<string, string> opt)
    {
        var model = AiSettings.Load().CreateModel();
        IReadOnlyList<DetectedField> fields = opt.TryGetValue("--fields", out var json)
            ? FieldsJson.Parse(await File.ReadAllTextAsync(json))
            : FormPipeline.Detect(pdf).Fields;
        if (!Confirm(AiFeatures.DescribeNaming(model, fields), opt)) return 1;
        Console.Error.WriteLine(L.F("Ai_Working", model.Describe));
        var r = await AiFeatures.NameFieldsAsync(model, pdf, fields);
        var output = opt.GetValueOrDefault("-o", Path.GetFileNameWithoutExtension(pdf) + "_fields_ai.json");
        await File.WriteAllTextAsync(output, FieldsJson.Serialize(r.Fields));
        Console.WriteLine(L.F("Ai_Named", Path.GetFileName(pdf), r.Fields.Count, r.Renamed, r.KindsChanged, output));
        return 0;
    }

    private static async Task<int> Fill(string pdf, Dictionary<string, string> opt)
    {
        if (!opt.TryGetValue("--profile", out var profilePath)) { Console.Error.WriteLine(L.Get("Ai_NoProfile")); return 2; }
        var model = AiSettings.Load().CreateModel();
        var profile = await File.ReadAllTextAsync(profilePath);
        var fields = AcroFormFields.List(pdf);
        if (!Confirm(AiFeatures.DescribeFill(model, fields, profile), opt)) return 1;
        Console.Error.WriteLine(L.F("Ai_Working", model.Describe));
        var plan = await AiFeatures.PlanFillAsync(model, pdf, fields, profile);

        foreach (var (name, value) in plan.Values)
            Console.WriteLine($"  {name} = {(plan.SecretFields.Contains(name) ? $"•••• ({value.Count(char.IsAsciiDigit)} digits)" : value)}");
        foreach (var n in plan.Notes) Console.WriteLine(L.F("Ai_Note", n));

        var output = opt.GetValueOrDefault("-o", Path.GetFileNameWithoutExtension(pdf) + "_filled.pdf");
        OpenTypeFont? font = opt.TryGetValue("--embed-font", out var f)
            ? OpenTypeFont.Load(f, opt.TryGetValue("--font-index", out var fi) && int.TryParse(fi, out var n2) ? n2 : 0)
            : null;
        var result = AcroFormFields.Fill(pdf, output, plan.Values, font);
        foreach (var n in result.Notes) Console.WriteLine(L.F("Ai_Note", n));
        Console.WriteLine(L.F("Ai_Filled", result.Filled, output));
        return 0;
    }

    private static async Task<int> Ask(string pdf, string question, Dictionary<string, string> opt)
    {
        var model = AiSettings.Load().CreateModel();
        if (!Confirm(AiFeatures.DescribeAsk(model, pdf), opt)) return 1;
        Console.Error.WriteLine(L.F("Ai_Working", model.Describe));
        Console.WriteLine(await AiFeatures.AskAsync(model, pdf, question));
        return 0;
    }

    // ------------------------------------------------------------------ consent
    /// <summary>Shows what will leave the machine and asks; a local model needs no question.</summary>
    internal static bool Confirm(AiPayload payload, Dictionary<string, string> opt)
    {
        if (!payload.OffDevice)
        {
            Console.Error.WriteLine(L.F("Ai_Local", payload.Model));
            return true;
        }
        Console.Error.WriteLine(L.F("Ai_WillSend", payload.Model, payload.Summary()));
        if (opt.ContainsKey("--yes")) return true;
        if (Console.IsInputRedirected) { Console.Error.WriteLine(L.Get("Ai_NeedYes")); return false; }
        Console.Error.Write(L.Get("Ai_Confirm"));
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer is "y" or "yes" or "o" or "oui" or "j" or "ja" or "はい" or "是" or "예" or "네") return true;
        Console.Error.WriteLine(L.Get("Ai_Cancelled"));
        return false;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static (string[] Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var pos = new List<string>();
        var opt = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--yes") opt["--yes"] = "";
            else if (args[i].StartsWith('-') && i + 1 < args.Length) opt[args[i]] = args[++i];
            else pos.Add(args[i]);
        }
        return ([.. pos], opt);
    }
}
