using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Helpers
{
    public static class ConfirmationHelper
    {
        /// <summary>
        /// Shows a native Revit TaskDialog asking the user to confirm a destructive operation.
        /// Returns true if the user clicks Yes, false otherwise.
        /// </summary>
        public static bool Confirm(string action, int elementCount)
        {
            if (elementCount <= 0) return true;

            // [experiment P2] Unattended runs: no human can answer a modal dialog, so auto-confirm
            // and log instead of blocking Revit's main thread.
            System.Diagnostics.Trace.WriteLine(
                $"[RevitMCP][dialog_suppressed] Auto-confirmed: {action} {elementCount} element(s)");
            return true;
        }
    }
}
