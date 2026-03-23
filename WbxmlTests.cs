using System;
using System.Linq;
using System.Xml.Linq;

namespace EasArchiver;

internal static class WbxmlTests
{
    private static readonly XNamespace NsFolderHier = "FolderHierarchy:";
    private static readonly XNamespace NsProvision  = "Provision:";

    // ── Known server responses captured with -vvv ─────────────────────────────

    // Provision Phase-1 response: <Provision><Status>165</Status></Provision>
    private const string HexProvision =
        "03016A00000E454B03313635000101";

    // FolderSync response (763 bytes, verbatim from -vvv output)
    private const string HexFolderSync =
        "03016A000007564C0331000152033100014E5703323100014F48033100014903300001470341726368697600014A0331320001014F480332000149033000014703417566676162656E00014A03370001014F480333000149033000014703456E7477C3BC72666500014A03330001014F48033400014903300001470347656CC3B6736368746520456C656D656E746500014A03340001014F480335000149033000014703476573656E6465746520456C656D656E746500014A03350001014F4803360001490330000147034A6F75726E616C00014A0331310001014F4803370001490330000147034A756E6B2D452D4D61696C00014A0331320001014F4803380001490330000147034B616C656E64657200014A03380001014F48033900014903380001470346656965727461676520696E20446575747363686C616E6400014A0331330001014F48033130000149033800014703476562757274737461676500014A0331330001014F480331310001490330000147034B6F6E74616B746500014A03390001014F480331320001490330000147034E6F74697A656E00014A0331300001014F48033133000149033000014703506F737461757367616E6700014A03360001014F48033134000149033000014703506F737465696E67616E6700014A03320001014F480331350001490330000147035253532D41626F6E6E656D656E747300014A0331320001014F4803313600014903300001470353796E6368726F6E6973696572756E677370726F626C656D6500014A0331320001014F48033137000149033136000147034B6F6E666C696B746500014A0331320001014F48033138000149033136000147034C6F6B616C65204665686C657200014A0331320001014F48033139000149033136000147035365727665726665686C657200014A0331320001014F480332300001490330000147035665726C6175662064657220556E74657268616C74756E6700014A03310001014F48035249000149033000014703526563697069656E74496E666F00014A0331390001010101";

    // ── Test runner ───────────────────────────────────────────────────────────

    public static int RunAll()
    {
        Console.WriteLine("=== WBXML Decoder Tests ===\n");
        int pass = 0, fail = 0;

        // ── Provision ─────────────────────────────────────────────────────────
        Test("Provision: root = Provision:Provision",
            HexProvision,
            xml => xml.Name == NsProvision + "Provision",
            ref pass, ref fail);

        Test("Provision: Status = 165",
            HexProvision,
            xml => xml.Descendants(NsProvision + "Status").FirstOrDefault()?.Value == "165",
            ref pass, ref fail);

        // ── FolderSync ────────────────────────────────────────────────────────
        Test("FolderSync: root = FolderHierarchy:FolderSync",
            HexFolderSync,
            xml => xml.Name == NsFolderHier + "FolderSync",
            ref pass, ref fail);

        Test("FolderSync: Status = 1",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "Status").FirstOrDefault()?.Value == "1",
            ref pass, ref fail);

        Test("FolderSync: SyncKey = 1",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "SyncKey").FirstOrDefault()?.Value == "1",
            ref pass, ref fail);

        Test("FolderSync: Count = 21",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "Count").FirstOrDefault()?.Value == "21",
            ref pass, ref fail);

        Test("FolderSync: Add elements exist",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "Add").Any(),
            ref pass, ref fail);

        Test("FolderSync: first folder = Archiv",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "Add")
                      .First()
                      .Element(NsFolderHier + "DisplayName")?.Value == "Archiv",
            ref pass, ref fail);

        Test("FolderSync: last folder = RecipientInfo",
            HexFolderSync,
            xml => xml.Descendants(NsFolderHier + "Add")
                      .Last()
                      .Element(NsFolderHier + "DisplayName")?.Value == "RecipientInfo",
            ref pass, ref fail);

        // // ── Large response from file ─────────────────────────────────────────
        // var hexFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "biganswer.hex");
        // if (File.Exists(hexFile))
        // {
        //     Test("BigAnswer: decode 116KB Sync response",
        //         File.ReadAllText(hexFile).Trim(),
        //         xml => xml.Name.LocalName == "Sync",
        //         ref pass, ref fail);
        // }

        // ── Round-trip: Encode → Decode ───────────────────────────────────────
        Test("Round-trip: FolderSync request encode/decode",
            roundTrip: true,
            assert: xml => xml.Name          == NsFolderHier + "FolderSync" &&
                           xml.Element(NsFolderHier + "SyncKey")?.Value == "0",
            ref pass, ref fail);

        Console.WriteLine($"\n{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    // ── Overload for hex-based tests ──────────────────────────────────────────

    private static void Test(
        string name,
        string hex,
        Func<XElement, bool> assert,
        ref int pass, ref int fail)
    {
        try
        {
            var clean = hex.Replace(" ", "");
            var bytes = Convert.FromHexString(clean);
            var xml   = EasWbxml.Decode(bytes);

            if (assert(xml))
            {
                Ok(name); pass++;
            }
            else
            {
                Fail(name, $"decoded root: {xml.Name}  full: {xml}"); fail++;
            }
        }
        catch (Exception ex) { Fail(name, ex.Message); fail++; }
    }

    // ── Overload for round-trip tests ─────────────────────────────────────────

    private static void Test(
        string name,
        bool roundTrip,
        Func<XElement, bool> assert,
        ref int pass, ref int fail)
    {
        _ = roundTrip; // just a disambiguator
        try
        {
            var input = new XElement(
                NsFolderHier + "FolderSync",
                new XElement(NsFolderHier + "SyncKey", "0"));

            var bytes = EasWbxml.Encode(input);
            var back  = EasWbxml.Decode(bytes);

            if (assert(back))
            {
                Ok(name); pass++;
            }
            else
            {
                Fail(name, $"decoded: {back}"); fail++;
            }
        }
        catch (Exception ex) { Fail(name, ex.Message); fail++; }
    }

    private static void Ok(string name)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  PASS  {name}");
        Console.ResetColor();
    }

    private static void Fail(string name, string detail)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  FAIL  {name}");
        Console.WriteLine($"        {detail}");
        Console.ResetColor();
    }
}
