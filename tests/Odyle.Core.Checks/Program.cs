// The Core checks: every group runs and every failure is reported (CheckRunner).
//   dotnet run --project tests/Odyle.Core.Checks -c Release                       all groups
//   dotnet run --project tests/Odyle.Core.Checks -c Release -- --only packet,pvp   some of them
//   dotnet run --project tests/Odyle.Core.Checks -c Release -- <url> <code>        also against a live backend
var run = new CheckRunner(args);
run.Add("Packet", PacketChecks.Run);
run.Add("Capture", CaptureChecks.Run);
run.Add("Location", LocationChecks.Run);
run.Add("Group", GroupChecks.Run);
run.Add("Pvp", PvpChecks.Run);
run.Add("Identity", IdentityChecks.Run);
run.Add("Checklist", ChecklistChecks.Run);
run.Add("Localization", LocalizationChecks.Run);
run.Add("Connection", ConnectionChecks.Run);
run.Add("Desktop", DesktopChecks.Run);
run.Add("Discord", DiscordChecks.Run);
run.Add("Setup", SetupChecks.Run);
run.Add("Realtime", RealtimeChecks.Run);
run.Add("RealtimeClient", RealtimeClientChecks.Run);
run.Add("Answer", AnswerChecks.Run);
run.Add("Clock", ClockChecks.Run);
run.Add("Party", PartyChecks.Run);
run.Add("Changelog", ChangelogChecks.Run);
run.Add("Contract", ContractChecks.Run);
run.Add("Release", ReleaseChecks.Run);
if (run.Pairing.Length > 0) run.Add("Live", () => LiveChecks.Run(run.Pairing));
return await run.RunAsync();
