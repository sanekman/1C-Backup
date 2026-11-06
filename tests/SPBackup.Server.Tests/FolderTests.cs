using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class FolderTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private async Task<Guid> User() => (await fixture.Store.FindUserAsync("administrator", default))!.Id;
    private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];
    private async Task<Guid> Folder(string name, Guid? parent = null) => await fixture.Store.CreateFolderAsync(await User(), new(name, parent));
    private async Task<EnrollmentResponse> Enroll(string name, Guid? folder, string computer = "COMP")
    {
        var invite = await fixture.Store.InviteAsync(await User(), new("Test", DisplayName: name, FolderId: folder), default);
        return await fixture.Store.EnrollAsync(new(invite.Code, Guid.NewGuid(), computer), default);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<string> Get(string path)
    { using var r = await fixture.Administrator.GetAsync(path); r.EnsureSuccessStatusCode(); return await r.Content.ReadAsStringAsync(); }
    private async Task<string> Form(string path, string handler, Dictionary<string, string> values)
    {
        var html = await Get(path);
        var form = Regex.Matches(html, "<form\\b[^>]*>[\\s\\S]*?</form>").Cast<Match>().Select(m => m.Value)
            .Single(f => f.Contains("value=\"" + handler + "\""));
        values["__RequestVerificationToken"] = ServerFixture.AntiToken(form);
        values["_handler"] = handler;
        using var r = await fixture.Administrator.PostAsync(path, new FormUrlEncodedContent(values));
        r.EnsureSuccessStatusCode(); return await r.Content.ReadAsStringAsync();
    }
    [ServerFact]
    public async Task BrowserInvitationCreatesSubfolderAndFriendlyNameIndependentOfLegacyClientMachineName()
    {
        var root = await Folder(Unique("Клиенты"));
        var html = await Form("connect?folderId=" + root, "invitation", new()
        {
            ["Model.Name"] = "Бухгалтерия — основной сервер", ["Model.FolderId"] = root.ToString(),
            ["Model.NewFolderName"] = Unique("Офис"), ["Model.Minutes"] = "30"
        });
        Assert.Contains("Приглашение готово", WebUtility.HtmlDecode(html));
        var code = Regex.Matches(html, "<textarea\\b[^>]*>([\\s\\S]*?)</textarea>").Cast<Match>()
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).Single(ServerTokens.IsValid);
        Assert.True(ServerTokens.IsValid(code));
        using var client = fixture.Client();
        using var result = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(code, Guid.NewGuid(), "COMP-TECHNICAL")));
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        var enrolled = JsonSerializer.Deserialize<EnrollmentResponse>(await result.Content.ReadAsStringAsync(), Json)!;
        var agent = (await fixture.Store.AgentAsync(enrolled.AgentId, default))!;
        Assert.Equal("Бухгалтерия — основной сервер", agent.Name); Assert.Equal("COMP-TECHNICAL", agent.ComputerName);
        var folder = Assert.Single(await fixture.Store.FoldersAsync(), f => f.Id == agent.FolderId);
        Assert.Equal(root, folder.ParentId);
        var page = WebUtility.HtmlDecode(await Get($"?folder={root}"));
        Assert.Contains(agent.Name, page); Assert.Contains("COMP-TECHNICAL", page);
        Assert.Contains("aria-current=\"page\"", page);
    }
    [ServerFact]
    public async Task BrowserCanRenameAndMoveAgentWithoutChangingTokenConfigurationOrHistory()
    {
        var agent = await Enroll("First", null);
        var config = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var run = new SPBackup.Server.Monitoring.MonitoredRun(Guid.NewGuid(), Guid.NewGuid(), "Old copy", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Succeeded", 0, false, "Full", []);
        await fixture.Store.SaveRunAsync(agent.AgentId, run, default);
        var root = await Folder(Unique("Компания"));
        var html = await Form($"agents/{agent.AgentId}/edit", "agent-identity", new()
        {
            ["Model.Name"] = "Новый сервер <script>пример</script>", ["Model.FolderId"] = root.ToString(),
            ["Model.NewFolderName"] = ""
        });
        Assert.Contains("Имя и папка сохранены", WebUtility.HtmlDecode(html));
        var saved = (await fixture.Store.AgentAsync(agent.AgentId, default))!;
        Assert.Equal(root, saved.FolderId); Assert.Equal("COMP", saved.ComputerName);
        Assert.Equal("Новый сервер <script>пример</script>", saved.Name);
        Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
        Assert.Equal(config, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        Assert.Equal(run.RunId, Assert.Single((await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs).RunId);
        var page = await Get($"agents/{agent.AgentId}");
        Assert.DoesNotContain("<script>пример</script>", page);
        Assert.Contains("&lt;script&gt;", page);
        Assert.Contains("AgentIdentityChanged", JsonSerializer.Serialize(await fixture.Store.AuditPageAsync(0)));
    }
    [ServerFact]
    public async Task HierarchyFilteringIncludesDescendantsAndPreservesUnfiledAndAllViews()
    {
        var root = await Folder(Unique("Клиент")); var child = await Folder("Филиал", root);
        var a = await Enroll("Parent", root); var b = await Enroll("Child", child); var c = await Enroll("Unfiled", null);
        var branch = await fixture.Store.AgentsAsync(0, default, root);
        Assert.Equal(2, branch.Total); Assert.Contains(branch.Agents, x => x.AgentId == a.AgentId); Assert.Contains(branch.Agents, x => x.AgentId == b.AgentId);
        Assert.Single((await fixture.Store.AgentsAsync(0, default, child)).Agents);
        var unfiled = await fixture.Store.AgentsAsync(0, default, unfiledOnly: true);
        Assert.Contains(unfiled.Agents, x => x.AgentId == c.AgentId); Assert.DoesNotContain(unfiled.Agents, x => x.AgentId == a.AgentId);
        using var response = await fixture.Administrator.GetAsync($"api/v1/management/agents?folderId={root}"); response.EnsureSuccessStatusCode();
        var wire = JsonSerializer.Deserialize<AgentPage>(await response.Content.ReadAsStringAsync(), Json)!;
        Assert.Equal(2, wire.Total); Assert.All(wire.Agents, x => Assert.Equal("COMP", x.ComputerName));
        var html = await Get("?folder=" + root); Assert.Contains("folder=" + root, html); Assert.Contains("connect?folderId=" + root, html);
    }
    [ServerFact]
    public async Task FolderBrowserCreatesNestedFoldersRenamesAndDeletesOnlyEmptyOnes()
    {
        var name = Unique("Организации");
        var html = await Form("folders", "folder", new() { ["Model.Name"] = name, ["Model.ParentId"] = "", ["Model.Id"] = "" });
        Assert.Contains("Папка сохранена", WebUtility.HtmlDecode(html));
        var root = Assert.Single(await fixture.Store.FoldersAsync(), f => f.Name == name);
        await Form("folders?parent=" + root.Id, "folder", new() { ["Model.Name"] = "Отдел", ["Model.ParentId"] = root.Id.ToString(), ["Model.Id"] = "" });
        var child = Assert.Single(await fixture.Store.FoldersAsync(), f => f.ParentId == root.Id);
        Assert.Equal(2, child.Depth);
        var denied = await Form("folders?edit=" + root.Id, "delete-folder", new() { ["Removal.Id"] = root.Id.ToString() });
        Assert.Contains("содержит агентов, подпапки", WebUtility.HtmlDecode(denied));
        await Form("folders?edit=" + child.Id, "folder", new() { ["Model.Id"] = child.Id.ToString(), ["Model.Name"] = "Другой отдел", ["Model.ParentId"] = root.Id.ToString() });
        Assert.Contains(await fixture.Store.FoldersAsync(), f => f.Id == child.Id && f.Name == "Другой отдел");
        var deleted = await Form("folders?edit=" + child.Id, "delete-folder", new() { ["Removal.Id"] = child.Id.ToString() });
        Assert.Contains("Пустая папка удалена", WebUtility.HtmlDecode(deleted));
        Assert.DoesNotContain(await fixture.Store.FoldersAsync(), f => f.Id == child.Id);
    }
    [ServerFact]
    public async Task FolderCyclesDuplicateNamesAndExcessiveDepthAreRejected()
    {
        var user = await User(); var root = await Folder(Unique("Root")); var child = await Folder("Отдел", root);
        var duplicate = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.CreateFolderAsync(user, new("  отдел  ", root)));
        Assert.Equal("folder_name_exists", duplicate.Code);
        var cycle = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.UpdateFolderAsync(user, root, new("Root", child)));
        Assert.Equal("folder_cycle", cycle.Code);
        var last = child;
        for (var depth = 3; depth <= ServerStore.MaximumFolderDepth; depth++) last = await Folder("Level", last);
        var deep = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.CreateFolderAsync(user, new("Too deep", last)));
        Assert.Equal("folder_depth_limit", deep.Code);
        Assert.Contains(await fixture.Store.FoldersAsync(), f => f.Id == last && f.Depth == 16);
    }
    [ServerFact]
    public async Task ConcurrentDuplicateCreationAndOppositeMovesCannotCorruptTheTree()
    {
        var user = await User(); var root = await Folder(Unique("Concurrent"));
        async Task<string> Create() { try { await fixture.Store.CreateFolderAsync(user, new("One", root)); return "ok"; } catch (ServerRequestException e) { return e.Code; } }
        var results = await Task.WhenAll(Create(), Create()); Assert.Single(results, x => x == "ok"); Assert.Single(results, x => x == "folder_name_exists");
        var a = await Folder("A", root); var b = await Folder("B", root);
        async Task<string> Move(Guid id, Guid parent, string name) { try { await fixture.Store.UpdateFolderAsync(user, id, new(name, parent)); return "ok"; } catch (ServerRequestException e) { return e.Code; } }
        var moved = await Task.WhenAll(Move(a, b, "A"), Move(b, a, "B"));
        Assert.Single(moved, x => x == "ok"); Assert.Single(moved, x => x == "folder_cycle");
        var folders = await fixture.Store.FoldersAsync(); var rootPath = folders.Single(f => f.Id == root).Path;
        Assert.Equal(4, folders.Count(f => f.Id == root || f.Path.StartsWith(rootPath + " / ", StringComparison.Ordinal)));
    }
    [ServerFact]
    public async Task ActiveInvitationsAndAgentsPreventDeletionAndOldReconnectPreservesAliasAndFolder()
    {
        var user = await User(); var folder = await Folder(Unique("Reserved"));
        var invite = await fixture.Store.InviteAsync(user, new("Invite", DisplayName: "Office", FolderId: folder), default);
        var blocked = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.DeleteFolderAsync(user, folder)); Assert.Equal("folder_not_empty", blocked.Code);
        var agent = await fixture.Store.EnrollAsync(new(invite.Code, Guid.NewGuid(), "COMP-A"), default);
        blocked = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.DeleteFolderAsync(user, folder)); Assert.Equal("folder_not_empty", blocked.Code);
        await fixture.Store.RevokeAsync(user, agent.AgentId, default);
        var next = await fixture.Store.InviteAsync(user, new("Old client invitation", agent.AgentId), default);
        await fixture.Store.EnrollAsync(new(next.Code, agent.AgentId, "COMP-B"), default);
        var saved = (await fixture.Store.AgentAsync(agent.AgentId, default))!;
        Assert.Equal("Office", saved.Name); Assert.Equal("COMP-B", saved.ComputerName); Assert.Equal(folder, saved.FolderId);
    }
    [ServerFact]
    public async Task ApiRequiresAdministratorAndCsrfAndRejectedRequestsDoNotLeaveFoldersBehind()
    {
        using var anonymous = fixture.Client();
        using var denied = await anonymous.PostAsync("api/v1/management/folders", ServerFixture.Json(new FolderRequest("Forbidden")));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var missingCsrf = fixture.Client(); await fixture.SignInAsync(missingCsrf);
        using var csrf = await missingCsrf.PostAsync("api/v1/management/folders", ServerFixture.Json(new FolderRequest("No CSRF")));
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        var name = Unique("Rollback");
        using var invalid = await fixture.Administrator.PutAsync($"api/v1/management/agents/{Guid.NewGuid()}/identity", ServerFixture.Json(new AgentIdentityRequest("Missing", NewFolderName: name)));
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.DoesNotContain(await fixture.Store.FoldersAsync(), f => f.Name == name);
        foreach (var bad in new string?[] { null, "", " ", "bad/path", "bad\\path", "..", "line\nname", new string('x', 129) })
        {
            using var badName = await fixture.Administrator.PostAsync("api/v1/management/folders", ServerFixture.Json(new FolderRequest(bad!)));
            Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        }
        var agent = await Enroll("No management access", null);
        using var agentClient = fixture.Client(); agentClient.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        using var forbidden = await agentClient.PostAsync("api/v1/management/folders", ServerFixture.Json(new FolderRequest("Agent cannot create folders")));
        Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
    }
    [ServerFact]
    public async Task FriendlyNameAndFolderSurviveRestartAndLegacyInvitationDefaultsToMachineName()
    {
        var agent = await fixture.EnrollAsync();
        var initial = (await fixture.Store.AgentAsync(agent.AgentId, default))!;
        Assert.Equal("Test agent", initial.Name); Assert.Equal(initial.Name, initial.ComputerName); Assert.Null(initial.FolderId);
        var folder = await Folder(Unique("Persistent"));
        await fixture.Store.UpdateAgentIdentityAsync(await User(), agent.AgentId, new("Friendly", folder));
        await fixture.RestartAsync();
        var saved = (await fixture.Store.AgentAsync(agent.AgentId, default))!;
        Assert.Equal("Friendly", saved.Name); Assert.Equal("Test agent", saved.ComputerName); Assert.Equal(folder, saved.FolderId);
        Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
    }
    [ServerFact]
    public async Task FolderPaginationKeepsFilterAndDoesNotMixOtherAgents()
    {
        var folder = await Folder(Unique("Paging"));
        await fixture.ExecuteAsync("""
            WITH added AS (
              INSERT INTO server_agents(id,name,computer_name,folder_id)
              SELECT gen_random_uuid(),'Paging agent','COMP',@folder FROM generate_series(1,52) RETURNING id
            ) INSERT INTO server_configurations(agent_id,revision,document)
            SELECT id,1,jsonb_build_object('agentId',id,'revision',1,'jobs','[]'::jsonb,'protocolVersion',1) FROM added
            """, ("folder", folder));
        var first = await fixture.Store.AgentsAsync(0, default, folder);
        var second = await fixture.Store.AgentsAsync(1, default, folder);
        Assert.Equal(52, first.Total); Assert.Equal(50, first.Agents.Count); Assert.Equal(2, second.Agents.Count);
        Assert.Equal(52, first.Agents.Concat(second.Agents).Select(a => a.AgentId).Distinct().Count());
        var page = WebUtility.HtmlDecode(await Get($"?folder={folder}&page=1"));
        Assert.Contains($"?folder={folder}&page=0", page);
        Assert.All(second.Agents, a => Assert.Equal(folder, a.FolderId));
    }

}
