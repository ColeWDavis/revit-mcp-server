using System;
using System.IO;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Helpers;
using revit_mcp_plugin.UI;
using revit_mcp_plugin.Utils;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var pluginDir = Path.GetDirectoryName(typeof(Application).Assembly.Location);
            McpLogger.Initialize(pluginDir);
            McpLogger.Info("Application", "Plugin starting");

            // [experiment P1] Removed: ClaudeDesktopConfigurator.EnsureConfigured() silently edited
            // Claude Desktop's config on every Revit start.

            // [experiment P3] Start the MCP listener automatically on the first Idling event
            // (a valid API context with a UIApplication), instead of waiting for a ribbon click.
            application.Idling += AutoStartOnIdling;

            // [experiment C2] event logger, snapshots and harness control channel
            try { revit_mcp_plugin.Experiment.ExperimentHooks.Init(application); }
            catch (Exception ex) { McpLogger.Error("Application", "Experiment hooks failed to initialise", ex); }

            // Register Dockable Panel
            try
            {
                application.RegisterDockablePane(
                    MCPDockablePaneProvider.PaneId,
                    "MCP Server",
                    new MCPDockablePaneProvider());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RevitMCP] Panel registration skipped: {ex.Message}");
            }

            RibbonPanel mcpPanel = application.CreateRibbonPanel("Revit MCP Plugin");

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "Revit MCP\r\n Switch",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            pushButtonData.ToolTip = "Open / Close mcp server";
            pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-16.png", UriKind.RelativeOrAbsolute));
            pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(pushButtonData);

            PushButtonData panelButtonData = new PushButtonData("ID_EXCMD_TOGGLE_MCP_PANEL", "MCP\r\n Panel",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.ToggleMCPPanel");
            panelButtonData.ToolTip = "Show / Hide MCP monitoring panel";
            panelButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/panel-16.png", UriKind.RelativeOrAbsolute));
            panelButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/panel-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(panelButtonData);

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Settings",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = "MCP Settings";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            return Result.Succeeded;
        }

        private void AutoStartOnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var uiApp = sender as UIApplication;
            if (uiApp == null) return;
            uiApp.Idling -= AutoStartOnIdling;
            try
            {
                var service = SocketService.Instance;
                if (!service.IsRunning)
                {
                    service.Initialize(uiApp);
                    service.Start();
                    McpLogger.Info("Application", $"Auto-started MCP listener, running={service.IsRunning}, port={service.Port}");
                }
            }
            catch (Exception ex)
            {
                McpLogger.Error("Application", "Auto-start of MCP listener failed", ex);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RevitMCP] Error during shutdown: {ex.Message}");
            }

            return Result.Succeeded;
        }
    }
}
