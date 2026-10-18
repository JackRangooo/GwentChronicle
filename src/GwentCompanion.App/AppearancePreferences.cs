using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GwentCompanion.App;

internal sealed record AppearanceState(bool ReducedTransparency=false);
internal static class AppearancePreferences
{
    internal static AppearanceState Current {get;private set;}=new();
    internal static bool IsAudit=>Environment.GetCommandLineArgs().Any(a=>a.Contains("smoke")||a.Contains("audit"));
    private static string PathName=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GwentVision","appearance.json");
    internal static void Load()
    {
        try {Current=File.Exists(PathName)?JsonSerializer.Deserialize<AppearanceState>(File.ReadAllText(PathName))??new():new();}
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){Current=new();}
        Apply(Current.ReducedTransparency,false);
    }
    internal static void Apply(bool reduced,bool save=true)
    {
        Current=new(reduced);var resources=Application.Current.Resources;
        if(reduced||SystemParameters.HighContrast) {
            resources["ShellMaterialBrush"]=new SolidColorBrush(Color.FromRgb(25,35,51));
            resources["ShellEdgeBrush"]=new SolidColorBrush(Color.FromRgb(75,88,107));
            resources["CanvasMaterialBrush"]=new SolidColorBrush(Color.FromRgb(11,16,24));
        } else {
            resources.Remove("ShellMaterialBrush");resources.Remove("ShellEdgeBrush");resources.Remove("CanvasMaterialBrush");
        }
        if(!save||IsAudit)return;
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);var temp=PathName+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(Current));File.Move(temp,PathName,true);
    }
}
