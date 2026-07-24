using System;
using System.Windows;
using CyberSnap.Services;

namespace CyberSnap.UI
{
    public partial class SettingsWindow
    {
        private void LoadToastButtonLayoutDesigner()
        {
            // No-op: designer section removed
        }

        private void RefreshToastButtonLayoutDesigner()
        {
            // No-op: designer section removed
        }

        private void RefreshEditorPreviewState()
        {
            // No-op: designer section removed
        }

        public void SyncConfirmPillShowLabels(bool show)
        {
            // No-op: designer section removed
        }

        public void RefreshConfirmPillDesigner()
        {
            // No-op: designer section removed
        }

        public void NavigateToCaptureConfirmPills()
        {
            CaptureTab.IsChecked = true;
            ApplyMainTabSelection();
        }
    }
}
