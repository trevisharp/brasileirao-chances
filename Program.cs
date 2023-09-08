using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

try
{
    await generate();
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}

async Task generate()
{
    var page = await getPage();
    var matches = await extractTables(page);

    System.Console.WriteLine(matches.Count());
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
        
        match.HomeTeam = find(
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
        
        match.AwayTeam = find(
            get(),
            "<span itemprop", "</span>",
            22
        );
    }
    else
    {
        get(); // discard

        match.HomeTeam = find(
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

        match.AwayTeam = reverse(
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
    var lastWrite = File.GetLastWriteTime("page.html");
    var today = DateTime.Today;
    var diff = lastWrite - today;
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

    public string HomeTeam { get; set; }
    public int HomeGoals { get; set; }

    public string AwayTeam { get; set; }
    public int AwayGoals { get; set; }

    public override string ToString() => IsComplete ?
        $"{HomeTeam} {HomeGoals} x {AwayGoals} {AwayTeam} [{Round}]" :
        $"{HomeTeam} x {AwayTeam} [{Round}]"; 
}