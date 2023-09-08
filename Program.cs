using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
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
    await extractTables(page);
}

async Task extractTables(string page)
{
    var days = page.Split("Dia de Jogo");

    await Task.Factory.StartNew(() => 
        Parallel.ForEach(days[1..], day =>
        {
            processDay(day);
        })
    );
}

void processDay(string dayHtml)
{
    var start = 0;
    var end = dayHtml.IndexOf("</tr", start + 1);
    
    for (int i = 0; i < 10; i++)
    {
        start = dayHtml.IndexOf("<tr", end + 1);
        end = dayHtml.IndexOf("</tr", start + 1);
        if (start == -1 || end == -1)
            break;
        
        var table = dayHtml.Substring(start, end - start);

        System.Console.WriteLine(table);
    }
}

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