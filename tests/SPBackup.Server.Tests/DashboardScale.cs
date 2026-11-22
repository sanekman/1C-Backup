using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SPBackup.Server.Tests;

internal static class DashboardScale
{
    internal static async Task MeasureAsync(ServerFixture fixture, string output, string repository, string passwordFile)
    {
        Directory.CreateDirectory(output);
        var folders = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await fixture.ExecuteAsync("""
            INSERT INTO server_agent_folders(id,parent_id,name,name_key) VALUES
                (@root,NULL,'Нагрузка','нагрузка'),(@branch,@root,'Филиал','филиал'),(@leaf,@branch,'Отдел','отдел');
            """, ("root", folders[0]), ("branch", folders[1]), ("leaf", folders[2]));
        var timings = new List<object>();
        foreach (var count in new[] { 100, 1000 })
        {
            await fixture.ExecuteAsync("""
                WITH agents AS (
                    INSERT INTO server_agents(id,name,computer_name,folder_id,received_utc,execution_available,reported_revision)
                    SELECT gen_random_uuid(), 'Scale agent ' || lpad(i::text,4,'0'), 'SCALE-' || i, @folder, now(), true, 1
                    FROM generate_series((SELECT count(*)::int + 1 FROM server_agents), @count) i
                    RETURNING id
                ), configurations AS (
                    INSERT INTO server_configurations(agent_id,revision,document)
                    SELECT id,1,jsonb_build_object('jobs','[]'::jsonb,'revision',1) FROM agents RETURNING agent_id
                ), monitored AS (
                    INSERT INTO server_monitoring(agent_id,document)
                    SELECT id, jsonb_build_object('sentUtc',now(),'agentVersion','scale','jobs',jsonb_build_array(
                        jsonb_build_object('jobId',gen_random_uuid(),'name','Daily files','sourceKind','FilesAndFolders',
                        'enabled',true,'schedule','Daily','destinationCount',1)),'pendingRuns',0,
                        'executionMode','Managed','configurationRevision',1,'backupExecutionAvailable',true) FROM agents
                    RETURNING agent_id,document
                )
                INSERT INTO server_runs(agent_id,run_id,job_id,finished_utc,document)
                SELECT agent_id,run_id,job_id,finished_at,jsonb_build_object('runId',run_id,'jobId',job_id,'jobName','Daily files',
                    'startedUtc',finished_at-interval '2 minutes','finishedUtc',finished_at,'status','Succeeded',
                    'warningCount',0,'incomplete',false,'destinations',jsonb_build_array(jsonb_build_object(
                    'index',0,'succeeded',true,'bytes',4096,'incomplete',false)))
                FROM monitored CROSS JOIN LATERAL (SELECT (document#>>'{jobs,0,jobId}')::uuid job_id) j
                CROSS JOIN LATERAL (SELECT gen_random_uuid() run_id, now()-make_interval(hours=>i) finished_at
                    FROM generate_series(1,5) i) r;
                """, ("folder", folders[2]), ("count", count));
            foreach (var (view, query) in new[] { ("classic", "?branch=all"), ("compact", "?view=compact&branch=all") })
            {
                var elapsed = new List<double>();
                var queryDuration = new List<double>();
                var measuring = false;
                using var listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == "Npgsql",
                    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStopped = activity => { if (measuring && activity.Kind == ActivityKind.Client) lock (queryDuration) queryDuration.Add(activity.Duration.TotalMilliseconds); }
                };
                ActivitySource.AddActivityListener(listener);
                var htmlBytes = 0;
                for (var run = 0; run < 9; run++)
                {
                    var started = Stopwatch.GetTimestamp();
                    measuring = run != 0;
                    using var response = await fixture.Administrator.GetAsync(query);
                    response.EnsureSuccessStatusCode();
                    htmlBytes = (await response.Content.ReadAsByteArrayAsync()).Length;
                    measuring = false;
                    if (run != 0) elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                elapsed.Sort();
                timings.Add(new { agents = count, view, treeDepth = 3, seededJobsPerAgent = 1, seededRunsPerAgent = 5, htmlBytes,
                    responseMedianMs = Median(elapsed), responseP95Ms = elapsed[^1], sampledSqlCommands = queryDuration.Count,
                    sqlCommandsPerRequest = queryDuration.Count / (double)elapsed.Count,
                    sqlTotalMs = Math.Round(queryDuration.Sum(), 2) });
                var start = new ProcessStartInfo("python3") { WorkingDirectory = repository, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var argument in new[] { "tests/server/dashboard_scale.py", fixture.Address.ToString(), passwordFile,
                    Path.Combine(output, $"browser-{count}-{view}.json"), count.ToString(System.Globalization.CultureInfo.InvariantCulture), view })
                    start.ArgumentList.Add(argument);
                using var browser = Process.Start(start)!;
                var error = browser.StandardError.ReadToEndAsync();
                var standard = browser.StandardOutput.ReadToEndAsync();
                await browser.WaitForExitAsync();
                Assert.True(browser.ExitCode == 0, await error + await standard);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "server.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Median(IReadOnlyList<double> values) => (values[values.Count / 2] + values[(values.Count - 1) / 2]) / 2;
}
