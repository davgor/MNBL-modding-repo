using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.ViewModelCollection.Input;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-46: the world preview panel (GUI/Prefabs/WorldPreview.xml) over a WorldPreview: the raster (WorldPreviewTextureProvider), a marker
    // per settlement and port at its own position, the kingdom regions' names, and the world's counts. Settlement kinds are told apart by shape
    // and size, not colour alone (a town a large light square, a castle a light square with a dark centre, a village a small dark dot, a port a
    // blue-edged square), and every label is text. Long names are shortened with an ellipsis; the place pointed at with the mouse, or selected
    // with Previous/Next place (the tab keys, the controller's shoulder buttons, or the two buttons), is shown in full below the map
    // (HoverText), with its kingdom's full name; every kingdom holds a town (FactionFactory dissolves the others), so walking the places names
    // every kingdom. Pointing at a kingdom's label shows its full name too. (A full kingdom list was tried and dropped: 40 names took about ten
    // lines and pushed the panel past a 1080-unit page with long texts, run 13920.)
    internal sealed class WorldPreviewViewModel : ViewModel
    {
        // The preview's size on the panel; positions are offsets inside it (the logical frame, x periodic, the seam at both edges; north up).
        public const float PreviewWidth = 800, PreviewHeight = 400;
        public const int LabelLength = 16, RegionLength = 22;
        private MBBindingList<WorldPreviewMarkerVM> markers = new MBBindingList<WorldPreviewMarkerVM>();
        private MBBindingList<WorldPreviewRegionVM> regions = new MBBindingList<WorldPreviewRegionVM>();
        private string countsText, legendText, identityText, textureError, textureErrorText, hoverText, previousPlaceText, nextPlaceText;
        private bool hasPreview, hasTextureError;
        private InputKeyItemVM previousPlaceKey, nextPlaceKey;
        private List<WorldPreviewMarkerVM> places = new List<WorldPreviewMarkerVM>();
        private WorldPreviewMarkerVM hovered;
        public WorldPreview Preview { get; private set; }
        public WorldPreviewMarkerVM Selected { get; private set; }
        public IList<WorldPreviewMarkerVM> Places { get { return places; } }

        public void Show(WorldPreview preview, IDictionary<string, string> counts, string legend)
        {
            Preview = preview;
            HasPreview = preview != null;
            Markers.Clear(); Regions.Clear(); places.Clear(); Selected = null; hovered = null; HoverText = "";
            PreviousPlaceText = WorldSetupViewModel.Text(WorldSetupText.PreviousPlace); NextPlaceText = WorldSetupViewModel.Text(WorldSetupText.NextPlace);
            if (preview == null) { CountsText = ""; LegendText = ""; IdentityText = ""; HasTextureError = false; TextureError = ""; TextureErrorText = ""; WorldPreviewTextures.Clear(); return; }
            foreach (var m in preview.Markers.OrderBy(m => m.Kind == "village" ? 0 : m.Kind == "port" ? 1 : m.Kind == "castle" ? 2 : 3).ThenBy(m => m.Id, StringComparer.Ordinal))
                Markers.Add(new WorldPreviewMarkerVM(m, Hover));
            // The places Previous/Next walk: towns and castles, west to east, then north to south.
            places = Markers.Where(m => m.IsTown || m.IsCastle).OrderBy(m => m.Marker.U).ThenBy(m => m.Marker.V).ThenBy(m => m.Marker.Id, StringComparer.Ordinal).ToList();
            // A kingdom of one town is named by its town's label; regions of two or more fortifications get their own; pointing at one names it in full.
            foreach (var r in preview.Regions.Where(r => r.Towns + r.Castles >= 2)) Regions.Add(new WorldPreviewRegionVM(r, HoverKingdom));
            CountsText = String.Join(" ", counts.Select(kv => kv.Value));
            LegendText = legend;
            IdentityText = preview.WorldContentId.Substring(0, 12);
            // The engine texture is made on the engine thread (this view model is refreshed there).
            HasTextureError = !WorldPreviewTextures.Show(preview);
            TextureError = HasTextureError ? WorldPreviewTextures.LastError : "";
            TextureErrorText = HasTextureError ? WorldSetupViewModel.Text(WorldSetupText.PreviewTextureFailed) : "";
        }

        // The place pointed at (null: the pointer left it; the selected place, if any, is shown again).
        internal void Hover(WorldPreviewMarkerVM marker)
        {
            hovered = marker;
            HoverText = Describe(marker ?? Selected);
        }

        // The kingdom label pointed at (null: left), in full.
        internal void HoverKingdom(WorldPreviewRegionVM region)
        {
            hovered = null;
            HoverText = region == null ? Describe(Selected) : WorldSetupViewModel.Text(WorldSetupText.KingdomHover, new Dictionary<string, string> { ["KINGDOM"] = region.FullName ?? "" });
        }

        // Previous/Next place (keys or buttons): one place to the west or east, wrapping.
        public void ExecutePreviousPlace() { Step(-1); }
        public void ExecuteNextPlace() { Step(1); }
        private void Step(int delta)
        {
            if (places.Count == 0) return;
            int index = Selected == null ? (delta > 0 ? -1 : 0) : places.IndexOf(Selected);
            index = ((index + delta) % places.Count + places.Count) % places.Count;
            if (Selected != null) Selected.IsSelected = false;
            Selected = places[index]; Selected.IsSelected = true;
            if (hovered == null) HoverText = Describe(Selected);
        }

        private string Describe(WorldPreviewMarkerVM marker)
        {
            if (marker == null || Preview == null) return "";
            string kingdom;
            Preview.KingdomNames.TryGetValue(marker.Marker.Kingdom ?? "", out kingdom);
            return WorldSetupViewModel.Text(WorldSetupText.Place(marker.Marker.Kind, !String.IsNullOrEmpty(kingdom), marker.Marker.Harbour), new Dictionary<string, string> { ["NAME"] = marker.Marker.Name ?? "", ["KINGDOM"] = kingdom ?? "" });
        }

        public override void OnFinalize()
        {
            base.OnFinalize();
            if (PreviousPlaceKey != null) PreviousPlaceKey.OnFinalize();
            if (NextPlaceKey != null) NextPlaceKey.OnFinalize();
        }

        [DataSourceProperty] public MBBindingList<WorldPreviewMarkerVM> Markers { get { return markers; } set { if (value != markers) { markers = value; OnPropertyChangedWithValue(value, "Markers"); } } }
        [DataSourceProperty] public MBBindingList<WorldPreviewRegionVM> Regions { get { return regions; } set { if (value != regions) { regions = value; OnPropertyChangedWithValue(value, "Regions"); } } }
        [DataSourceProperty] public bool HasPreview { get { return hasPreview; } set { if (value != hasPreview) { hasPreview = value; OnPropertyChangedWithValue(value, "HasPreview"); } } }
        [DataSourceProperty] public string CountsText { get { return countsText; } set { if (value != countsText) { countsText = value; OnPropertyChangedWithValue(value, "CountsText"); } } }
        [DataSourceProperty] public string LegendText { get { return legendText; } set { if (value != legendText) { legendText = value; OnPropertyChangedWithValue(value, "LegendText"); } } }
        [DataSourceProperty] public string IdentityText { get { return identityText; } set { if (value != identityText) { identityText = value; OnPropertyChangedWithValue(value, "IdentityText"); } } }
        [DataSourceProperty] public string HoverText { get { return hoverText; } set { if (value != hoverText) { hoverText = value; OnPropertyChangedWithValue(value, "HoverText"); } } }
        [DataSourceProperty] public string PreviousPlaceText { get { return previousPlaceText; } set { if (value != previousPlaceText) { previousPlaceText = value; OnPropertyChangedWithValue(value, "PreviousPlaceText"); } } }
        [DataSourceProperty] public string NextPlaceText { get { return nextPlaceText; } set { if (value != nextPlaceText) { nextPlaceText = value; OnPropertyChangedWithValue(value, "NextPlaceText"); } } }
        [DataSourceProperty] public InputKeyItemVM PreviousPlaceKey { get { return previousPlaceKey; } set { if (value != previousPlaceKey) { previousPlaceKey = value; OnPropertyChangedWithValue(value, "PreviousPlaceKey"); } } }
        [DataSourceProperty] public InputKeyItemVM NextPlaceKey { get { return nextPlaceKey; } set { if (value != nextPlaceKey) { nextPlaceKey = value; OnPropertyChangedWithValue(value, "NextPlaceKey"); } } }
        [DataSourceProperty] public bool HasTextureError { get { return hasTextureError; } set { if (value != hasTextureError) { hasTextureError = value; OnPropertyChangedWithValue(value, "HasTextureError"); } } }
        [DataSourceProperty] public string TextureError { get { return textureError; } set { if (value != textureError) { textureError = value; OnPropertyChangedWithValue(value, "TextureError"); } } }
        [DataSourceProperty] public string TextureErrorText { get { return textureErrorText; } set { if (value != textureErrorText) { textureErrorText = value; OnPropertyChangedWithValue(value, "TextureErrorText"); } } }
        [DataSourceProperty] public float Width { get { return PreviewWidth; } set { } }
        [DataSourceProperty] public float Height { get { return PreviewHeight; } set { } }
    }

    internal sealed class WorldPreviewMarkerVM : ViewModel
    {
        private const float LabelWidth = 150;
        private readonly WorldPreview.Marker marker;
        private readonly Action<WorldPreviewMarkerVM> hover;
        private bool isSelected;
        public WorldPreviewMarkerVM(WorldPreview.Marker marker, Action<WorldPreviewMarkerVM> hover) { this.marker = marker; this.hover = hover; }
        internal WorldPreview.Marker Marker { get { return marker; } }
        private float Size { get { return marker.Kind == "town" ? 12 : marker.Kind == "castle" ? 10 : marker.Kind == "port" ? 9 : marker.Harbour ? 7 : 5; } }
        // Labels near the east edge go west of the marker.
        private bool West { get { return marker.U > .85; } }
        public void ExecuteHover() { if (hover != null) hover(this); }
        public void ExecuteHoverEnd() { if (hover != null) hover(null); }
        [DataSourceProperty] public string Name { get { return WorldPreview.Shorten(marker.Name, WorldPreviewViewModel.LabelLength); } set { } }
        [DataSourceProperty] public string FullName { get { return marker.Name; } set { } }
        [DataSourceProperty] public float MarkerSize { get { return Size; } set { } }
        [DataSourceProperty] public float PositionX { get { return (float)(marker.U * WorldPreviewViewModel.PreviewWidth) - Size / 2; } set { } }
        [DataSourceProperty] public float PositionY { get { return (float)(marker.V * WorldPreviewViewModel.PreviewHeight) - Size / 2; } set { } }
        // The label's offset from the marker (the marker is the label's parent).
        [DataSourceProperty] public float LabelX { get { return West ? -LabelWidth - 3 : Size + 3; } set { } }
        [DataSourceProperty] public float LabelY { get { return Size / 2 - 10; } set { } }
        [DataSourceProperty] public bool IsTown { get { return marker.Kind == "town"; } set { } }
        [DataSourceProperty] public bool IsCastle { get { return marker.Kind == "castle"; } set { } }
        [DataSourceProperty] public bool IsVillage { get { return marker.Kind == "village" && !marker.Harbour; } set { } }
        // A harbour village (War Sails: fleets land there; the game gives it a port).
        [DataSourceProperty] public bool IsHarbourVillage { get { return marker.Kind == "village" && marker.Harbour; } set { } }
        [DataSourceProperty] public bool IsPort { get { return marker.Kind == "port"; } set { } }
        [DataSourceProperty] public bool ShowLabel { get { return marker.Kind == "town"; } set { } }
        [DataSourceProperty] public bool ShowLabelRight { get { return ShowLabel && !West; } set { } }
        [DataSourceProperty] public bool ShowLabelLeft { get { return ShowLabel && West; } set { } }
        [DataSourceProperty] public bool IsSelected { get { return isSelected; } set { if (value != isSelected) { isSelected = value; OnPropertyChangedWithValue(value, "IsSelected"); } } }
        [DataSourceProperty] public string Kind { get { return marker.Kind; } set { } }
    }

    internal sealed class WorldPreviewRegionVM : ViewModel
    {
        private readonly WorldPreview.Region region;
        private readonly Action<WorldPreviewRegionVM> hover;
        public WorldPreviewRegionVM(WorldPreview.Region region, Action<WorldPreviewRegionVM> hover) { this.region = region; this.hover = hover; }
        internal WorldPreview.Region Region { get { return region; } }
        public void ExecuteHover() { if (hover != null) hover(this); }
        public void ExecuteHoverEnd() { if (hover != null) hover(null); }
        [DataSourceProperty] public string Name { get { return WorldPreview.Shorten(region.Name, WorldPreviewViewModel.RegionLength); } set { } }
        [DataSourceProperty] public string FullName { get { return region.Name; } set { } }
        [DataSourceProperty] public float PositionX { get { return (float)(region.U * WorldPreviewViewModel.PreviewWidth) - 100; } set { } }
        [DataSourceProperty] public float PositionY { get { return (float)(region.V * WorldPreviewViewModel.PreviewHeight) + 10; } set { } }
        [DataSourceProperty] public string Color { get { return "#" + region.Color.ToString("X6", CultureInfo.InvariantCulture) + "FF"; } set { } }
    }
}
