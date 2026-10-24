using System.IO.Pipes;
using System.Text.Json.Nodes;
using MimeKit;
using MMail.Core.Agent;
using MMail.Core.Mail;
using MMail.Core.Models;
using MMail.Core.State;
using MMail.Core.Storage;

namespace MMail.Core.Tests;

public sealed class Fixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("mmail-test").FullName;
    public MailStore Store { get; }
    public FakeMailService Server { get; } = new();
    public MemoryCredentialStore Credentials { get; } = new();
    public MailController Model { get; private set; }
    public MailAccountConfig Account { get; } = MailAccountConfig.Gmail("Me", "me@gmail.com");

    public Fixture()
    {
        Environment.SetEnvironmentVariable("MMAIL_HOME", Dir);
        Store = new MailStore(Path.Combine(Dir, "mmail.db"));
        Model = new MailController(Store, Server, Credentials);
    }

    public async Task<MailController> WithAccount()
    {
        await Model.AddAccountAsync(Account, "app-password");
        return Model;
    }

    /// A fresh controller on the same database, as after a restart.
    public MailController Reopen() => Model = new MailController(Store, Server, Credentials);

    public void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class StoreTests
{
    [Fact]
    public async Task Messages_survive_a_restart_and_are_searchable()
    {
        using var f = new Fixture();
        f.Server.Deliver("INBOX", "Ann Smith", "ann@example.com", "Quarterly numbers", "The café budget is attached.");
        f.Server.Deliver("INBOX", "Bob", "bob@example.com", "Lunch?", "Tomorrow at noon");
        await f.WithAccount();

        var model = f.Reopen();
        Assert.Equal(2, model.Emails.Count);
        Assert.Single(f.Store.Search("quarter"));
        Assert.Single(f.Store.Search("cafe"));   // diacritics folded
        Assert.Equal("ann@example.com", f.Store.Contacts(10)[1].Email);
        Assert.Equal("app-password", f.Credentials.Get(f.Account.ImapPasswordKey));
    }

    [Fact]
    public void Prefs_round_trip()
    {
        using var f = new Fixture();
        f.Store.SetPref("pinned", new HashSet<string> { "a", "b" });
        Assert.Equal(2, f.Store.GetPref<HashSet<string>>("pinned")!.Count);
        Assert.Null(f.Store.GetPref<HashSet<string>>("missing"));
    }
}

public class ListTests
{
    [Fact]
    public async Task Inbox_shows_pinned_then_unread_then_everything_else()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.Now;
        f.Server.Deliver("INBOX", "Old read", "a@x.com", "Read 1", "x", seen: true, date: now.AddHours(-3));
        f.Server.Deliver("INBOX", "New unread", "b@x.com", "Unread 1", "x", seen: false, date: now.AddHours(-2));
        f.Server.Deliver("INBOX", "To pin", "c@x.com", "Pinned 1", "x", seen: true, date: now.AddHours(-5));
        var model = await f.WithAccount();
        model.TogglePin(model.Emails.Single(e => e.Subject == "Pinned 1"));

        var sections = model.Sections();
        Assert.Equal(["Pinned", "Unread", "Everything else"], sections.Select(s => s.Title));
        Assert.Equal("Unread 1", sections[1].Items.Single().Subject);

        // Marking read moves it to the rest; with nothing else unread, Unread disappears.
        model.SetRead(sections[1].Items[0], true);
        Assert.Equal(["Pinned", "Everything else"], model.Sections().Select(s => s.Title));

        // The Unread tab has no Unread section, and other folders have no sections at all.
        model.SetFilter(InboxFilter.Unread);
        Assert.Empty(model.VisibleEmails());
        model.SetFilter(InboxFilter.All);
        model.SetFolder("sent");
        Assert.Null(Assert.Single(model.Sections()).Title);
    }

    [Fact]
    public async Task Archive_waits_for_undo_then_moves_on_the_server()
    {
        using var f = new Fixture();
        f.Server.Deliver("INBOX", "Ann", "ann@x.com", "Hello", "x");
        var model = await f.WithAccount();
        Action? undo = null;
        model.Toast += t => undo = t.Action;

        var email = model.Emails.Single();
        model.Archive(email);
        Assert.Empty(model.VisibleEmails());
        undo!();
        Assert.Single(model.VisibleEmails());

        model.Archive(email);
        await Task.Delay(MailController.UndoWindow + TimeSpan.FromSeconds(1));
        Assert.Contains(f.Server.Calls, c => c.StartsWith("move INBOX") && c.EndsWith("[Gmail]/All Mail"));
        Assert.Empty(f.Server.Boxes["INBOX"]);
    }

    [Fact]
    public async Task Refresh_drops_mail_gone_from_the_server_and_adds_new_mail()
    {
        using var f = new Fixture();
        var first = f.Server.Deliver("INBOX", "Ann", "ann@x.com", "First", "x");
        var model = await f.WithAccount();
        f.Server.Boxes["INBOX"].RemoveAll(m => m.Header.Uid == first.Uid);
        f.Server.Deliver("INBOX", "Bob", "bob@x.com", "Second", "y");
        await model.RefreshAsync();
        Assert.Equal("Second", Assert.Single(model.VisibleEmails()).Subject);
    }
}

public class MimeTests
{
    [Fact]
    public void Html_becomes_readable_text()
    {
        var text = MimeTools.HtmlToText("<html><head><style>p{}</style></head><body><p>Hello&nbsp;<b>Ann</b></p><!-- x --><div>Bye</div></body></html>");
        Assert.Equal("Hello Ann\nBye", text);
    }

    [Fact]
    public void Recipients_are_parsed()
    {
        Assert.Equal(["ann@x.com", "bob@y.com"], MimeTools.ParseRecipients("Ann <ann@x.com>; bob@y.com, nobody"));
    }

    [Fact]
    public void Built_messages_parse_back_with_attachments()
    {
        var cfg = MailAccountConfig.Gmail("Me", "me@gmail.com");
        var draft = new ComposeDraft { To = "Ann <ann@x.com>", Subject = "Hi", Body = "Body text" };
        draft.Attachments.Add(new ComposeAttachment("notes.txt", "text/plain", "abc"u8.ToArray()));
        var parsed = MimeTools.Parse(MimeTools.Build(cfg, null, draft));
        Assert.Equal("Body text", parsed.Text);
        Assert.Equal("notes.txt", Assert.Single(parsed.Attachments).FileName);
    }

    [Fact]
    public void Remote_images_are_blocked_unless_trusted()
    {
        var html = "<html><head></head><body><img src=\"https://t.example/p.gif\"></body></html>";
        Assert.Contains("img-src data: cid:;", MimeTools.SafeHtml(html, false));
        Assert.Contains("https:", MimeTools.SafeHtml(html, true));
        Assert.Equal(1, MimeTools.CountRemoteImages(html));
    }
}

public class AgentTests
{
    private static readonly string[] MacTools =
    [
        "list_accounts", "list_folders", "list_messages", "search_messages", "get_message", "get_attachment",
        "save_attachment", "list_contacts", "list_labels", "show_message", "set_flags", "triage", "move_message",
        "snooze_message", "label_message", "compose_message", "get_compose", "update_compose", "add_attachment",
        "remove_attachment", "save_draft", "send_message",
    ];

    private static async Task<JsonObject> Call(McpServer server, string method, JsonObject? @params = null, int id = 1)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? [] };
        return JsonNode.Parse((await server.HandleLineAsync(request.ToJsonString()))!)!.AsObject();
    }

    private static async Task<JsonNode> Tool(McpServer server, string name, JsonObject? args = null)
    {
        var response = await Call(server, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? [] });
        var result = response["result"]!.AsObject();
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        if (result["isError"]?.GetValue<bool>() == true) throw new ToolError(text);
        return text.StartsWith('[') || text.StartsWith('{') ? JsonNode.Parse(text)! : JsonValue.Create(text);
    }

    [Fact]
    public async Task Speaks_mcp_with_the_same_22_tools_as_the_mac_app()
    {
        using var f = new Fixture();
        var server = AgentTools.CreateServer(f.Model);
        var init = await Call(server, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
        Assert.Equal("mmail", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        var tools = (await Call(server, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.Equal(MacTools, tools);
        Assert.Null(await server.HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(-32700, JsonNode.Parse((await server.HandleLineAsync("not json"))!)!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reads_triages_and_composes()
    {
        using var f = new Fixture();
        f.Server.Deliver("INBOX", "Ann", "ann@x.com", "Invoice", "Please pay", attachments: ("invoice.txt", "42 EUR"));
        f.Server.Deliver("INBOX", "Bob", "bob@x.com", "Lunch", "Noon?");
        var model = await f.WithAccount();
        var server = AgentTools.CreateServer(model);

        var accounts = (await Tool(server, "list_accounts")).AsArray();
        Assert.Equal(2, accounts[0]!["unread"]!.GetValue<int>());

        var list = (await Tool(server, "list_messages", new JsonObject { ["unread_only"] = true })).AsArray();
        Assert.Equal(2, list.Count);
        var invoiceId = list.First(m => m!["subject"]!.GetValue<string>() == "Invoice")!["id"]!.GetValue<string>();

        var message = await Tool(server, "get_message", new JsonObject { ["id"] = invoiceId, ["mark_read"] = true });
        Assert.Equal("Please pay", message["body"]!.GetValue<string>());
        Assert.Equal("invoice.txt", message["attachments"]![0]!["filename"]!.GetValue<string>());
        Assert.False(model.Find(invoiceId)!.Unread);

        var attachment = await server.HandleLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "get_attachment", ["arguments"] = new JsonObject { ["id"] = invoiceId } },
        }.ToJsonString());
        Assert.True(attachment!.Contains("42 EUR"), attachment);

        Assert.Single((await Tool(server, "search_messages", new JsonObject { ["query"] = "lunch" })).AsArray());

        await Tool(server, "label_message", new JsonObject { ["id"] = invoiceId, ["label"] = "To Pay" });
        Assert.Contains("to-pay", model.Find(invoiceId)!.Labels);

        await Tool(server, "triage", new JsonObject { ["id"] = invoiceId, ["action"] = "archive" });
        Assert.Equal("archive", model.Find(invoiceId)!.Folder);

        await Tool(server, "compose_message", new JsonObject { ["reply_to_id"] = invoiceId, ["body"] = "Paid, thanks." });
        Assert.Equal("ann@x.com", model.Compose!.To);
        Assert.StartsWith("Paid, thanks.", model.Compose.Body);
        Assert.Equal("Re: Invoice", model.Compose.Subject);

        // The user edits the composer; Claude reads the edit back.
        model.Compose.Subject = "Re: Invoice (paid)";
        Assert.Equal("Re: Invoice (paid)", (await Tool(server, "get_compose"))["subject"]!.GetValue<string>());

        await Assert.ThrowsAsync<ToolError>(() => Tool(server, "compose_message", new JsonObject { ["to"] = "x@y.com" }));
        await Assert.ThrowsAsync<ToolError>(() => Tool(server, "send_message", new JsonObject { ["confirm"] = false }));

        await Tool(server, "add_attachment", new JsonObject { ["text"] = "receipt", ["filename"] = "receipt.txt" });
        Assert.Single(model.Compose.Attachments);
        await Tool(server, "send_message", new JsonObject { ["confirm"] = true });
        Assert.Null(model.Compose);
        await Task.Delay(MailController.UndoWindow + TimeSpan.FromSeconds(1));
        var sent = Assert.Single(f.Server.Sent);
        Assert.Equal("Re: Invoice (paid)", sent.Subject);
        Assert.Equal("m100@test", sent.InReplyTo);
    }

    [Fact]
    public async Task Answers_over_the_local_pipe()
    {
        using var f = new Fixture();
        var pipeName = $"mmail-test-{Guid.NewGuid():N}"[..24];
        using var host = new AgentPipeServer(AgentTools.CreateServer(f.Model), new InlineDispatcher(), pipeName);
        host.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        var writer = new StreamWriter(client) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(client);
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, JsonNode.Parse(line!)!["id"]!.GetValue<int>());
    }
}

public class ClaudeSetupTests
{
    [Fact]
    public void Adds_mmail_to_claude_desktop_and_keeps_the_rest()
    {
        var dir = Directory.CreateTempSubdirectory("claude-config").FullName;
        var path = Path.Combine(dir, "Claude", "claude_desktop_config.json");
        Assert.True(ClaudeSetup.AddToConfig(path, @"C:\MMail\mmail-mcp.exe"));
        File.WriteAllText(path, """{"mcpServers":{"other":{"command":"x"}},"theme":"dark"}""");
        Assert.True(ClaudeSetup.AddToConfig(path, @"C:\MMail\mmail-mcp.exe"));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(@"C:\MMail\mmail-mcp.exe", json["mcpServers"]!["mmail"]!["command"]!.GetValue<string>());
        Assert.Equal("x", json["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal("dark", json["theme"]!.GetValue<string>());

        File.WriteAllText(path, "{ not json");
        Assert.False(ClaudeSetup.AddToConfig(path, "x"));
        Directory.Delete(dir, true);
    }
}
