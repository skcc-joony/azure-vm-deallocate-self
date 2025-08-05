// Program.cs
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;

const string imdsUrl =
    "http://169.254.169.254/metadata/instance?api-version=2021-02-01";

var http = new HttpClient(new HttpClientHandler { Proxy = null });   // 프록시 우회
http.DefaultRequestHeaders.Add("Metadata", "true");

Console.WriteLine("Retrieving VM metadata...");
JsonElement meta = await http.GetFromJsonAsync<JsonElement>(imdsUrl);

string sub = meta.GetProperty("compute").GetProperty("subscriptionId").GetString()!;
string rg  = meta.GetProperty("compute").GetProperty("resourceGroupName").GetString()!;
string vm  = meta.GetProperty("compute").GetProperty("name").GetString()!;

Console.WriteLine($"Deallocating {rg}/{vm}...");

ArmClient arm = new ArmClient(new DefaultAzureCredential());
VirtualMachineResource vmRes = arm.GetVirtualMachineResource(
    VirtualMachineResource.CreateResourceIdentifier(sub, rg, vm));

ArmOperation op = await vmRes.DeallocateAsync(Azure.WaitUntil.Completed); // :contentReference[oaicite:4]{index=4}
Console.WriteLine($"HTTP {(op.GetRawResponse().Status)} — request accepted. "
                + "Session will close shortly.");