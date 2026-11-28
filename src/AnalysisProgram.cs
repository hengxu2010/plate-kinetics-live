// Standalone Plate Kinetics Analysis desktop application (WebView2, zero service/tray, GitHub auto-updater).
// Opens .pk, .pkproj, .json, .xlsx, .csv, .txt, .qps, .ruc files directly via double-click, drag-and-drop, or Open menu.
using System;

namespace SAAControl
{
    static class AnalysisProgram
    {
        [STAThread]
        static int Main(string[] argv) => AnalysisRunner.Run("PlateKineticsAnalysis", argv);
    }
}
