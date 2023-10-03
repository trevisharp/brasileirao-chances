using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

const int ADJUST = 160;
const int SIMULROUNDS = 200_000;

ApplicationConfiguration.Initialize();

var form = new Form();
form.WindowState = FormWindowState.Maximized;

ProgressBar progressBar = new ProgressBar();
progressBar.Dock = DockStyle.Top;
form.Controls.Add(progressBar);

PictureBox pb = new PictureBox();
pb.Dock = DockStyle.Fill;
form.Controls.Add(pb);

form.Load += async delegate
{
    var bmp = new Bitmap(pb.Width, pb.Height);
    pb.Image = bmp;
    var g = Graphics.FromImage(bmp);
    g.Clear(Color.White);
    
    await generate(p => progressBar.Value = (int)(100 * p));


};

Application.Run(form);

async Task<Team[]> generate(Action<float> callback)
{
    var page = await getPage();
    var matches = await extractTables(page);
    var teams = computeTeams(matches);

    await Task.Run(() =>
        monteCarlo(SIMULROUNDS, 3, matches, teams, callback)
    );

    return teams;
}

void monteCarlo(
    int N, int stability,
    IEnumerable<Match> matches, 
    IEnumerable<Team> originalTeams,
    Action<float> callback)
{
    for (int n = 0; n < N; n++)
    {
        if (n % 100 == 99)
            callback(n / (float)N);
        simulate(matches, originalTeams, stability);
    }
}

float random(int stability)
{
    float total = 0;
    for (int i = 0; i < stability; i++)
        total += 2 * Random.Shared.NextSingle() - 1;
    return total / stability;
}

void simulateMatch(Match match, IEnumerable<Team> teams, int stability)
{
    var homeTeam = teams
        .FirstOrDefault(t => t.Name == match.HomeTeamName);
    var awayTeam = teams
        .FirstOrDefault(t => t.Name == match.AwayTeamName);

    var eloDiff = homeTeam.Elo - awayTeam.Elo;
    var expected = 1 / (1 + MathF.Exp(-eloDiff / 200));
    var result = expected + random(stability);
    var diff = result - expected;
    
    if (result > .7)
        homeTeam.Points += 3;
    else if (result < -.8)
        awayTeam.Points += 3;
    else
    {
        homeTeam.Points++;
        awayTeam.Points++;
    }
}

void simulate(
    IEnumerable<Match> matches, 
    IEnumerable<Team> originalTeams,
    int stability)
{
    var teams = originalTeams.Clone().ToArray();
    var futureMatches = 
        from m in matches
        where !m.IsComplete
        orderby m.Round
        select m;
    
    foreach (var match in futureMatches)
        simulateMatch(match, teams, stability);
        
    var resultTeams = teams
        .OrderByDescending(t => t.Points)
        .Select(t => originalTeams
            .FirstOrDefault(o => o.Name == t.Name)
        ).ToArray();
    
    resultTeams[0].ChampionCount++;

    for (int i = 0; i < 4; i++)
        resultTeams[i].ContinentalCount++;
    for (int i = 4; i < 6; i++)
        resultTeams[i].QualifyContinentalCount++;
    for (int i = 6; i < 12; i++)
        resultTeams[i].SubContinentalCount++;
    for (int i = 16; i < 20; i++)
        resultTeams[i].RelegationCount++;
}

void computeMatch(Match match)
{
    var deltaGoals = 0.9f * match.HomeGoals - 1.1f * match.AwayGoals;
    var result = 1 / (1 + MathF.Exp(-deltaGoals));

    var eloDiff = match.HomeTeam.Elo - match.AwayTeam.Elo;
    var expected = 1 / (1 + MathF.Exp(-eloDiff / 200));

    var diff = result - expected;

    if (match.HomeGoals == match.AwayGoals)
    {
        match.HomeTeam.Points++;
        match.AwayTeam.Points++;
    }
    else if (match.HomeGoals > match.AwayGoals)
        match.HomeTeam.Points += 3;
    else match.AwayTeam.Points += 3;

    match.HomeTeam.Elo += ADJUST * diff;
    match.AwayTeam.Elo -= ADJUST * diff;
}

Team[] computeTeams(IEnumerable<Match> matches)
{
    var teams = new List<Team>();

    foreach (var match in matches)
    {
        var team = teams
            .FirstOrDefault(t => t.Name == match.HomeTeamName);
        if (team is null)
        {
            team = new Team
            {
                Elo = 1500,
                Name = match.HomeTeamName,
                Points = 0
            };
            teams.Add(team);
        }
        
        match.HomeTeam = team;
    }

    foreach (var match in matches)
    {
        var team = teams
            .FirstOrDefault(t => t.Name == match.AwayTeamName);
        if (team is null)
            continue;
        
        match.AwayTeam = team;
    }

    foreach (var match in matches.OrderBy(m => m.Round))
    {
        if (!match.IsComplete)
            continue;
        
        computeMatch(match);
    }

    return teams.ToArray();
}

async Task<Match[]> extractTables(string page)
{
    var days = page.Split("Dia de Jogo");

    ConcurrentQueue<Match> matches = new ConcurrentQueue<Match>();

    await Task.Factory.StartNew(() => 
        Parallel.ForEach(days[1..], day =>
        {
            foreach (var match in processDay(day))
            {
                matches.Enqueue(match);
            }
        })
    );

    return matches.ToArray();
}

List<Match> processDay(string dayHtml)
{
    List<Match> result = new();
    int day = int.Parse(dayHtml.Substring(0, 3).Trim());
    
    var isCurrent = dayHtml.Count() > 50_000;

    var start = 0;
    var end = dayHtml.IndexOf("</tr", start + 1);

    for (int i = 0; i < 10; i++)
    {
        start = dayHtml.IndexOf("<tr class=\"match", end + 1);
        end = isCurrent ? 
            dayHtml.IndexOf("link ac sm-invisible", start + 1)
          : dayHtml.IndexOf("</tr", start + 1);
        if (start == -1 || end == -1)
            break;
        
        var row = dayHtml.Substring(start, end - start);

        var data = processRow(row, isCurrent);
        if (data is null)
            continue;
        
        result.Add(data);
    }

    foreach (var match in result)
        match.Round = day;

    return result;
}

Match processRow(string row, bool crr = false)
{
    if (row is null)
        return null;
    
    Match match = new Match();
    match.IsComplete = row.Contains("match complete");
    var get = getDataReader(row);

    if (crr)
    {
        get(); // discard
        get(); // discard
        
        match.HomeTeamName = find(
            get(),
            "<span itemprop", "</span>",
            22
        );

        for (int i = 0; i < 40; i++)
            get(); // discard
        
        var scoretr = get();
        if (match.IsComplete)
        {
            var score = find(
                scoretr,
                "<span class=\"bold ft-score\">", "</span>",
                28
            );
            var scoreData = score.Split(' ');
            match.HomeGoals = int.Parse(scoreData[0]);
            match.AwayGoals = int.Parse(scoreData[2]);
        }
        
        get();
        
        match.AwayTeamName = find(
            get(),
            "<span itemprop", "</span>",
            22
        );
    }
    else
    {
        get(); // discard

        match.HomeTeamName = find(
            get(),
            "span", "span",
            crr ? 21 : 5, 2
        );

        var scoretr = get();
        if (match.IsComplete)
        {
            var score = find(
                scoretr,
                "span", "span",
                27, 2
            );
            var scoreData = score.Split(' ');
            match.HomeGoals = int.Parse(scoreData[0]);
            match.AwayGoals = int.Parse(scoreData[2]);
        }

        match.AwayTeamName = reverse(
            find(
                reverse(get()),
                ">a/<",
                ">\"",
                4, 0
            )
        );
    }

    return match;
}

Func<string> getDataReader(string text)
{
    var start = 0;
    return () =>
    {
        if (start == -1)
            return null;
        
        return getData(text, ref start);
    };
}

string getData(string text, ref int start)
{
    start = text.IndexOf("<td", start + 1);
    if (start == -1)
        return null;
    
    var end = text.IndexOf("/td>", start + 1);
    if (end == -1)
        return null;
    
    var data = text.Substring(start, end - start);
    start = end;
    return data;
}

string find(string text, string start, string end, int padleft = 0, int padright = 0, int index = 0)
{
    if (text.Length < index + 1)
        return null;

    index = text.IndexOf(start, index + 1);
    if (index == -1)
        return null;
    
    var endind = text.IndexOf(end, index + 1);
    if (endind == -1)
        return null;
    
    var indexA = index + padleft;
    var indexB = endind - padright;

    var data = text.Substring(indexA, indexB - indexA);
    return data;
}

string reverse(string text)
    => string.Concat((text ?? "").Reverse());

async Task<string> getPage()
{
    var hasCache = File.Exists("page.html");
    var hasInternet = await testConnectivity();

    if (!hasInternet && hasCache)
        return await loadPageFromCache();
    
    if (hasInternet && !hasCache)
        return await getOnlineAndSave();
    
    if (!hasInternet && !hasCache)
        throw new Exception("We dont has internet and dont exist a cache of data.");

    if (isCacheRecently())
        return await loadPageFromCache();
    
    return await getOnlineAndSave();
}

bool isCacheRecently()
{
    var lastAccess = File.GetLastAccessTime("page.html").Date;
    var today = DateTime.Today;
    var diff = today - lastAccess;
    return diff.TotalDays < 1;
}

async Task<string> getOnlineAndSave()
{
    var page = await getOnlinePage();

    if (!File.Exists("page.html"))
        File.CreateText("page.html").Close();

    await File.WriteAllTextAsync("page.html", page);

    return page;
}

async Task<string> loadPageFromCache()
{
    var content = await File.ReadAllTextAsync("page.html");
    
    return content;
}

async Task<string> getOnlinePage()
{
    const string dataPath = "https://footystats.org/pt/brazil/serie-a/fixtures";

    var http = new HttpClient();
    var response = await http.GetAsync(dataPath);

    if (!response.IsSuccessStatusCode)
        throw new Exception($"Request Failed with {response.StatusCode} status code.");

    var content = await response.Content.ReadAsStringAsync();
    return content;
}

async Task<bool> testConnectivity()
{
    var ping = new Ping();
    var result = await ping.SendPingAsync(
        "google.com", 3000, 
        new byte[32], 
        new PingOptions()
    );
    return result.Status == IPStatus.Success;
}

public class Match
{
    public int Round { get; set; }
    public bool IsComplete { get; set; }

    public Team HomeTeam { get; set;}
    public string HomeTeamName { get; set; }
    public int HomeGoals { get; set; }

    public Team AwayTeam { get; set; }
    public string AwayTeamName { get; set; }
    public int AwayGoals { get; set; }

    public override string ToString() => IsComplete ?
        $"{HomeTeamName} {HomeGoals} x {AwayGoals} {AwayTeamName} [{Round}]" :
        $"{HomeTeamName} x {AwayTeamName} [{Round}]"; 
}

public class Team
{
    public string Name { get; set; }
    public int Points { get; set; }
    public float Elo { get; set; }

    public int ChampionCount { get; set; }
    public int ContinentalCount { get; set; }
    public int QualifyContinentalCount { get; set; }
    public int SubContinentalCount { get; set; }
    public int RelegationCount { get; set; }

    public Team Clone()
        => new Team
        {
            Name = this.Name,
            Points = this.Points,
            Elo = this.Elo
        };

    public override string ToString() =>
        $"{Name} \t {Points} \t {Elo}";
}

public static class TeamExtension
{
    public static IEnumerable<Team> Clone(this IEnumerable<Team> teams)
    {
        foreach (var team in teams)
            yield return team.Clone();
    }
}