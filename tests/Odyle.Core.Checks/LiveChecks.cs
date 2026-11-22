using Odyle.Core;

// Against a running backend (the dashboard's integration suite passes its URL and a pairing code):
// pairing, characters, readings, realtime and the lasting pairing. The suite looks for the two
// "... check passed" lines below.
static class LiveChecks
{
    public static async Task Run(string[] args)
    {
        var pairing = ConnectionDetails.Parse(args[^1], args.Length == 2 ? args[0] : "http://127.0.0.1:8090");
        using var api = new PartyApi(pairing.Endpoint); var paired = await api.PairAsync(pairing.Code);
        if ((await api.SignInAsync()).Record.Id != paired.Record.Id) throw new Exception("Pairing changed account identity.");
        // Dashboard 1.0.0 and newer name their version; this app shows a notice for a server that names none or an older one.
        Console.WriteLine($"The test backend names dashboard version {api.ServerVersion ?? "(none)"}{(ServerVersions.Outdated(api.ServerVersion) ? ", older than " + ServerVersions.Minimum + ": the app would show its notice" : "")}.");
        var session = await api.StartSessionAsync();
        var saved = await api.SaveCharacterAsync(" Main ", " EU ", "GLOBAL_EU");
        if (saved.CharacterName != "Main" || saved.Server != "EU" || saved.User != paired.Record.Id) throw new Exception("Saving character identity failed.");
        var character = await api.ReportAsync(new PacketReading(session.SessionId, 1, "Main", "EU", "GLOBAL_EU", 0, "test-fixture-v1"));
        if (saved.Id != character.Id) throw new Exception("First game reading duplicated the saved character.");
        await api.HeartbeatAsync(session.SessionId, character.Id); await api.InvalidateAsync(character.Id, session.SessionId);
        // PvP stats are off for a new account, and the server knows the PvP route: the test backend lists
        // only its own adapter, so it refuses the app's with the message the app stops reading at. A
        // server from before PvP stats (0.57 or older) has neither.
        if (session.PvpStats == true) throw new Exception("A new account must not have PvP stats on.");
        try { await api.PvpAsync(session.SessionId, character.Id, [new("0123456789abcdef0123456789abcdef", "kill", "Vexa", "", "", "", "", "", 0)]); throw new Exception("The test backend accepted the Global PvP adapter."); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest && error.Message == PartyApi.PvpAdapterRefused || error.StatusCode == System.Net.HttpStatusCode.NotFound && session.PvpStats is null) { }
        if (!(await api.GetCharactersAsync()).Any(c => c.Id == character.Id && c.Value == 0 && c.Confirmed && !c.Valid)) throw new Exception("Character persistence failed.");
        await api.GetRoomsAsync(); Console.WriteLine("Windows account pairing and persistent character API check passed.");
        await RealtimeChecks.RunLive(api, paired.Record.Id, session.SessionId, character.Id);
        // The pairing outlives its token: a request with an expired token signs in with the key and is repeated.
        var key = paired.DeviceKey!;
        using var later = new PartyApi(pairing.Endpoint, "expired-token", key);
        await later.GetRoomsAsync();
        if (later.Token is null or "expired-token" || (await later.GetCharactersAsync()).All(c => c.Id != character.Id)) throw new Exception("An expired token did not sign in again with the device key.");
        if ((await later.SignInAsync()).Record.Id != paired.Record.Id) throw new Exception("Device sign-in changed account identity.");
        await later.UnpairAsync();
        using var forgotten = new PartyApi(pairing.Endpoint, null, key);
        try { await forgotten.SignInAsync(); throw new Exception("An unpaired device key still signed in."); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        Console.WriteLine("Windows lasting pairing check passed.");
    }
}
