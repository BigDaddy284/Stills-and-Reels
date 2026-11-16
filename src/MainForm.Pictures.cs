using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>The picture editor's side of the main window: tools, selections, adjustments, layers and their menus.</summary>
    internal sealed partial class MainForm
    {
        // adjustments being tried out (shown as a preview until Apply)
        PicSurface adjustPreview, adjustSource;
        int adjustLayerId;
        PicAdjustments adjustShown;
        string adjustName = "Adjust";
        readonly System.Windows.Forms.Timer adjustTimer = new System.Windows.Forms.Timer { Interval = 25 };

        ToolStripMenuItem miSelectAllPic, miDeselect, miInvertSel, miCutSel, miDeleteSel, miCropSel, miDuplicateObj;
        ToolStripMenuItem miImage, miResize, miCanvas, miStraighten, miTrim, miAutoFix, miLevels, miCurves, miFilters, miLayerMenu;

        static bool IsShapeTool(EditTool t) { return t == EditTool.Line || t == EditTool.Arrow || t == EditTool.Rect || t == EditTool.RoundRect || t == EditTool.Ellipse; }

        /// <summary>Tools that share one colour and size setting (all the shape tools keep theirs together).</summary>
        static EditTool ToolKey(EditTool t) { return IsShapeTool(t) ? EditTool.Line : t; }

        static int MaxSizeFor(EditTool t)
        {
            if (t == EditTool.Text) return 200;
            if (t == EditTool.Highlighter || t == EditTool.Eraser || t == EditTool.Step) return 60;
            return 30;
        }

        void WirePictureEditor()
        {
            sidebar.Options = canvas.Options;
            sidebar.FillPicked += OnFillPicked;
            sidebar.OptionsChanged += () => { canvas.RefreshTool(); };
            sidebar.AdjustChanged += () => { adjustName = "Adjust"; adjustTimer.Stop(); adjustTimer.Start(); };
            sidebar.LayerSelected += i => { if (edit == null) return; canvas.CommitPaste(); DropAdjust(true); edit.SetActive(i); };
            sidebar.LayerVisibilityToggled += i => { if (edit != null) edit.SetLayerVisible(i, !edit.State.Layers[i].Visible); };
            sidebar.LayerOpacityChanged += v => { if (edit != null) edit.SetLayerOpacity(edit.ActiveIndex, v / 100f); };
            adjustTimer.Tick += (s, e) => { adjustTimer.Stop(); RunAdjustPreview(sidebar.Adjust); };
            canvas.ColorPicked += c => { SetPictureColor(c, false); Flash("Colour picked: #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")); };
            canvas.ObjectSelectionChanged += (s, e) => SyncSidebar();
            canvas.Hint += t => Flash(t);
        }

        void LoadPictureSettings()
        {
            var red = Color.FromArgb(255, 0xE8, 0x11, 0x23);
            toolColors[EditTool.Line] = settings.GetColor("ShapeColor", red);
            toolColors[EditTool.Text] = settings.GetColor("TextColor", red);
            toolColors[EditTool.Step] = settings.GetColor("StepColor", red);
            toolColors[EditTool.Fill] = settings.GetColor("FillColor", Color.White);
            toolSizes[EditTool.Line] = settings.GetInt("ShapeSize", 4);
            toolSizes[EditTool.Text] = settings.GetInt("TextSize", 28);
            toolSizes[EditTool.Step] = settings.GetInt("StepSize", 12);
            var o = canvas.Options;
            o.FontName = settings.GetString("TextFont", "Segoe UI");
            o.Bold = settings.GetBool("TextBold", false);
            o.Italic = settings.GetBool("TextItalic", false);
            o.Tolerance = Math.Max(0, Math.Min(255, settings.GetInt("Tolerance", 32)));
            o.Contiguous = settings.GetBool("WandContiguous", true);
            o.RedactPixelate = settings.GetBool("HidePixelate", true);
            o.RedactStrength = Math.Max(3, Math.Min(60, settings.GetInt("HideStrength", 12)));
        }

        void SavePictureSettings()
        {
            settings.Set("ShapeColor", toolColors[EditTool.Line]);
            settings.Set("TextColor", toolColors[EditTool.Text]);
            settings.Set("StepColor", toolColors[EditTool.Step]);
            settings.Set("FillColor", toolColors[EditTool.Fill]);
            settings.Set("ShapeSize", toolSizes[EditTool.Line]);
            settings.Set("TextSize", toolSizes[EditTool.Text]);
            settings.Set("StepSize", toolSizes[EditTool.Step]);
            var o = canvas.Options;
            settings.Set("TextFont", o.FontName);
            settings.Set("TextBold", o.Bold);
            settings.Set("TextItalic", o.Italic);
            settings.Set("Tolerance", o.Tolerance);
            settings.Set("WandContiguous", o.Contiguous);
            settings.Set("HidePixelate", o.RedactPixelate);
            settings.Set("HideStrength", o.RedactStrength);
        }

        // ==================================================================== sidebar state

        /// <summary>The slider value (on-screen pixels) that matches a selected item's size in the picture.</summary>
        int SizeOf(PicObject o)
        {
            float onScreen = o.Width * canvas.Zoom / Theme.Scale;
            if (o is StepObject) onScreen /= 2.2f;
            return Math.Max(1, (int)Math.Round(onScreen));
        }

        void SyncPictureSidebar()
        {
            var st = new SidebarState { Picture = edit != null, Tool = tool };
            var key = ToolKey(tool);
            KeyValuePair<string, Color>[] pal = tool == EditTool.Highlighter ? Palettes.Highlighter : Palettes.Ink;
            Color c;
            st.Color = toolColors.TryGetValue(key, out c) ? c : Color.Black;
            int size;
            st.Size = toolSizes.TryGetValue(key, out size) ? size : 4;
            st.MaxSize = MaxSizeFor(tool);
            st.Palette = Palettes.Colors(pal);
            if (edit != null)
            {
                st.CanUndo = edit.CanUndo || HasPendingAdjust; st.CanRedo = edit.CanRedo;
                st.UndoName = HasPendingAdjust ? "adjustments" : Lower(edit.UndoName); st.RedoName = Lower(edit.RedoName);
                st.HasInk = edit.HasInk;
                st.HasSelection = edit.Selection != null;
                st.Floating = canvas.HasFloating;
                st.Selected = canvas.SelectedObject;
                st.Doc = edit.State;
                st.ActiveLayer = edit.ActiveIndex;
                if (st.Selected != null)
                {
                    st.MaxSize = st.Selected is TextObject ? 200 : st.Selected is StepObject ? 60 : st.Selected is Stroke && ((Stroke)st.Selected).Tool == EditTool.Highlighter ? 60 : 30;
                    st.Size = Math.Min(st.MaxSize, SizeOf(st.Selected));
                    var stroke = st.Selected as Stroke;
                    if (stroke != null && stroke.Tool == EditTool.Highlighter) st.Palette = Palettes.Colors(Palettes.Highlighter);
                    else st.Palette = Palettes.Colors(Palettes.Ink);
                }
            }
            sidebar.SetState(st);
        }

        static string Lower(string s) { return string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1); }

        void OnPictureEditChanged()
        {
            if (edit != null && adjustSource != null)
            {
                // the layer being adjusted changed under the preview: start the preview again, or drop it
                if (edit.State.IndexOf(adjustLayerId) < 0 || !ReferenceEquals(edit.ActiveLayer.Pixels, adjustSource) || edit.ActiveLayer.Id != adjustLayerId) DropAdjust(true);
                else { adjustTimer.Stop(); adjustTimer.Start(); }
            }
            SyncSidebar();
            UpdateUi();
        }

        // ==================================================================== colour, size, fill, font

        /// <summary>A colour picked in the sidebar or from the picture: goes to the selected item, and becomes the tool's colour.</summary>
        void SetPictureColor(Color c, bool fromSidebar)
        {
            var sel = canvas.SelectedObject;
            if (sel != null && fromSidebar)
            {
                canvas.ChangeSelected(o => o.WithColor(Color.FromArgb(o.Color.A == 0 ? 255 : o.Color.A, c)), "Colour");
                if (sel is Stroke) { SyncSidebar(); return; }
            }
            var key = ToolKey(tool);
            if (tool == EditTool.Picker) key = ToolKey(lastDrawingTool == EditTool.Picker ? EditTool.Pen : lastDrawingTool);
            if (!toolColors.ContainsKey(key)) key = EditTool.Pen;
            // the highlighter keeps its see-through strength whatever colour is picked
            toolColors[key] = Color.FromArgb(255, c);
            ApplyToolToCanvas();
            SyncSidebar();
        }

        void SetPictureSize(int v)
        {
            var sel = canvas.SelectedObject;
            if (sel != null && sel.HasWidth)
            {
                float inPicture = v * Theme.Scale / canvas.Zoom;
                if (sel is StepObject) inPicture = Math.Max(9f, v * Theme.Scale * 2.2f) / canvas.Zoom;
                canvas.ChangeSelected(o => o.WithWidth(Math.Max(0.5f, inPicture)), "Size");
            }
            var key = ToolKey(tool);
            if (toolSizes.ContainsKey(key) && (sel == null || !(sel is Stroke)))
            {
                toolSizes[key] = v;
                ApplyToolToCanvas();
            }
        }

        void OnFillPicked(Color c)
        {
            canvas.Options.Fill = c;
            if (canvas.SelectedObject != null && canvas.SelectedObject.HasFill) canvas.ChangeSelected(o => o.WithFill(c), "Fill");
            canvas.RestyleTextBox();
            SyncSidebar();
        }

        void ChooseFont()
        {
            var text = canvas.SelectedObject as TextObject;
            var o = canvas.Options;
            using (var dlg = new FontDialog { ShowEffects = false, AllowScriptChange = false, MinSize = 6, MaxSize = 200, FontMustExist = true })
            {
                try
                {
                    dlg.Font = new Font(text != null ? text.FontName : o.FontName, 12f,
                        ((text != null ? text.Bold : o.Bold) ? FontStyle.Bold : 0) | ((text != null ? text.Italic : o.Italic) ? FontStyle.Italic : 0));
                }
                catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SetFont(dlg.Font.FontFamily.Name, dlg.Font.Bold, dlg.Font.Italic);
            }
        }

        void SetFont(string name, bool? bold, bool? italic)
        {
            var o = canvas.Options;
            var text = canvas.SelectedObject as TextObject;
            if (name != null) o.FontName = name;
            if (bold.HasValue) o.Bold = bold.Value;
            if (italic.HasValue) o.Italic = italic.Value;
            if (text != null) canvas.ChangeSelected(x => ((TextObject)x).WithFont(name ?? text.FontName, bold ?? text.Bold, italic ?? text.Italic), "Font");
            canvas.RestyleTextBox();
            SyncSidebar();
        }

        // ==================================================================== commands from the sidebar and menus

        void OnPictureCommand(string cmd)
        {
            if (cmd == "font") { ChooseFont(); return; }
            if (cmd.StartsWith("font.bold:")) { SetFont(null, cmd.EndsWith("1"), null); return; }
            if (cmd.StartsWith("font.italic:")) { SetFont(null, null, cmd.EndsWith("1")); return; }
            if (edit == null) { SystemSounds(); return; }
            if (cmd.StartsWith("crop:"))
            {
                var parts = cmd.Split(':');
                int w = int.Parse(parts[1]), h = int.Parse(parts[2]);
                canvas.SetCropAspect(w == 0 ? 0f : w < 0 ? edit.Size.Width / (float)edit.Size.Height : w / (float)h);
                return;
            }
            switch (cmd)
            {
                case "paste.place": canvas.CommitPaste(); SyncSidebar(); break;
                case "paste.cancel": canvas.CancelPaste(); SyncSidebar(); break;
                case "sel.all": edit.Selection = PicSelection.All(edit.Size); break;
                case "sel.none": edit.Selection = null; break;
                case "sel.invert": edit.Selection = PicSelection.Invert(edit.Selection, edit.Size); break;
                case "sel.crop": CropToSelection(); break;
                case "sel.delete": canvas.ClearSelectionPixels(); break;
                case "sel.feather": FeatherSelection(); break;
                case "sel.copy": CopySelection(false); break;
                case "sel.cut": CopySelection(true); break;
                case "obj.delete": canvas.DeleteSelected(); break;
                case "obj.duplicate": canvas.DuplicateSelected(); break;
                case "obj.front": canvas.ReorderSelected(true); break;
                case "obj.back": canvas.ReorderSelected(false); break;
                case "adjust.apply": ApplyPendingAdjust(); break;
                case "adjust.reset": DropAdjust(true); break;
                case "adjust.auto": AutoFix(); break;
                case "adjust.looks": ShowLooksMenu(); break;
                case "adjust.levels": ShowLevels(); break;
                case "adjust.curves": ShowCurves(); break;
                case "pic.resize": ShowResize(); break;
                case "pic.canvas": ShowCanvasSize(); break;
                case "pic.straighten": ShowStraighten(); break;
                case "pic.trim": TrimEdges(); break;
                case "layer.add": Prepare(); edit.AddLayer(edit.NextLayerName("Layer"), null, 0, 0); break;
                case "layer.dup": Prepare(); edit.DuplicateLayer(edit.ActiveIndex); break;
                case "layer.delete": Prepare(); if (edit.State.Layers.Length > 1) edit.DeleteLayer(edit.ActiveIndex); break;
                case "layer.up": Prepare(); edit.MoveLayer(edit.ActiveIndex, edit.ActiveIndex + 1); break;
                case "layer.down": Prepare(); edit.MoveLayer(edit.ActiveIndex, edit.ActiveIndex - 1); break;
                case "layer.merge": Prepare(); edit.MergeDown(edit.ActiveIndex); break;
                case "layer.flatten": Prepare(); edit.FlattenLayers(); break;
                case "layer.blend": ShowBlendMenu(); break;
                case "layer.rename": RenameLayer(); break;
                case "save.layers": SavePictureAs(true); break;
            }
        }

        /// <summary>Settles anything half-done (a paste being placed, text being typed, sliders not applied) before a whole-picture step.</summary>
        void Prepare()
        {
            canvas.EndTextEdit(true);
            canvas.CommitPaste();
            ApplyPendingAdjust();
        }

        void EnsurePictureEdit()
        {
            if (mode != AppMode.PictureEdit) EnterPictureEdit(null);
        }

        // ==================================================================== selections

        void CropToSelection()
        {
            if (edit == null || edit.Selection == null) return;
            Prepare();
            var r = edit.Selection.Bounds;
            edit.Crop(r);
        }

        void FeatherSelection()
        {
            if (edit == null || edit.Selection == null) return;
            var n = PicDialog.AskNumber(this, "Soften the selection", "Soften the edge by", 1, 250, 8, "px");
            if (n.HasValue && edit != null && edit.Selection != null) edit.Selection = edit.Selection.Feather(n.Value, edit.Size);
        }

        /// <summary>Copies what is shown inside the selection (all layers together); cutting also clears it on the active layer.</summary>
        unsafe void CopySelection(bool cut)
        {
            if (edit == null || edit.Selection == null) { CopyImage(); return; }
            canvas.EndTextEdit(true);
            ApplyPendingAdjust();
            var sel = edit.Selection;
            try
            {
                using (var flat = edit.FlattenSurface())
                using (var part = flat.Clone(sel.Bounds))
                {
                    // outside the selection becomes see-through
                    for (int y = 0; y < part.Height; y++)
                    {
                        uint* p = part.Row(y);
                        for (int x = 0; x < part.Width; x++)
                        {
                            uint m = sel.At(sel.Bounds.X + x, sel.Bounds.Y + y);
                            if (m != 255) p[x] = m == 0 ? 0u : PicPaint.Scale(p[x], m);
                        }
                    }
                    using (var bmp = part.ToBitmap()) PutOnClipboard(bmp);
                }
                if (cut) canvas.ClearSelectionPixels();
                Flash(cut ? "Cut to the clipboard" : "Copied the selection");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't copy the selection: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void PutOnClipboard(Bitmap bmp)
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, bmp);
            var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            data.SetData("PNG", false, png);
            Clipboard.SetDataObject(data, true);
        }

        // ==================================================================== adjustments

        bool HasPendingAdjust { get { return adjustPreview != null && adjustShown != null && !adjustShown.IsIdentity; } }

        /// <summary>Shows a set of adjustments on the active layer without changing it yet.</summary>
        bool RunAdjustPreview(PicAdjustments a)
        {
            if (edit == null) return false;
            var layer = edit.ActiveLayer;
            if (layer.Pixels == null)
            {
                if (!a.IsIdentity) Flash("This layer has only drawings. Pick the layer with the picture, or flatten the layers first.");
                DropAdjust(true);
                return false;
            }
            if (a.IsIdentity) { DropAdjust(false); SyncSidebar(); return true; }
            canvas.CommitPaste();
            if (adjustPreview == null || !ReferenceEquals(adjustSource, layer.Pixels))
            {
                if (adjustPreview != null) adjustPreview.Dispose();
                adjustSource = layer.Pixels;
                adjustLayerId = layer.Id;
                adjustPreview = new PicSurface(adjustSource.Width, adjustSource.Height);
            }
            adjustShown = a;
            PicAdjust.Apply(adjustSource, adjustPreview, a, edit.Selection, layer.X, layer.Y);
            edit.Compositor.SetPreview(layer.Id, adjustPreview, null);
            canvas.Invalidate();
            SyncSidebar();
            return true;
        }

        /// <summary>Makes the previewed adjustments a real (undoable) change.</summary>
        void ApplyPendingAdjust()
        {
            adjustTimer.Stop();
            if (edit == null || adjustPreview == null) return;
            if (adjustShown == null || adjustShown.IsIdentity || edit.ActiveLayer.Id != adjustLayerId || !ReferenceEquals(edit.ActiveLayer.Pixels, adjustSource)) { DropAdjust(true); return; }
            // make sure the preview holds the sliders as they are right now
            if (ReferenceEquals(adjustShown, sidebar.Adjust)) PicAdjust.Apply(adjustSource, adjustPreview, adjustShown, edit.Selection, edit.ActiveLayer.X, edit.ActiveLayer.Y);
            var px = adjustPreview;
            adjustPreview = null; adjustSource = null; adjustShown = null;
            edit.Compositor.ClearPreview();
            sidebar.ResetAdjust();
            edit.ReplaceActivePixels(adjustName, px);
        }

        /// <summary>Forgets the previewed adjustments.</summary>
        void DropAdjust(bool resetSliders)
        {
            adjustTimer.Stop();
            bool had = adjustPreview != null;
            if (edit != null && had) edit.Compositor.ClearPreview();
            if (adjustPreview != null) adjustPreview.Dispose();
            adjustPreview = null; adjustSource = null; adjustShown = null;
            if (resetSliders) sidebar.ResetAdjust();
            if (had) { canvas.Invalidate(); SyncSidebar(); }
        }

        PicHistogram ActiveHistogram()
        {
            var l = edit.ActiveLayer;
            return l.Pixels != null ? PicAdjust.Histogram(l.Pixels, edit.Selection, l.X, l.Y) : new PicHistogram();
        }

        bool ReadyToAdjust()
        {
            if (current == null && edit == null) { SystemSounds(); return false; }
            EnsurePictureEdit();
            if (edit == null) return false;
            Prepare();
            if (edit.ActiveLayer.Pixels == null)
            {
                Flash("This layer has only drawings. Pick the layer with the picture, or flatten the layers first.");
                return false;
            }
            return true;
        }

        void AutoFix()
        {
            if (!ReadyToAdjust()) return;
            var lv = PicAdjust.AutoLevels(ActiveHistogram());
            if (lv.IsIdentity) { Flash("This picture is already well balanced"); return; }
            var curve = lv.ToCurve();
            adjustName = "Auto fix";
            if (RunAdjustPreview(new PicAdjustments { CurveR = curve, CurveG = curve, CurveB = curve })) ApplyPendingAdjust();
        }

        void ShowLooksMenu()
        {
            var m = new ContextMenuStrip();
            foreach (var kv in PicAdjustments.Looks)
            {
                var look = kv;
                m.Items.Add(look.Key, null, (s, e) => ApplyLook(look.Key, look.Value));
            }
            Theme.StyleMenu(m);
            m.Closed += (s, e) => BeginInvoke((MethodInvoker)m.Dispose);
            m.Show(Cursor.Position);
        }

        /// <summary>A look from Filters goes onto the sliders, where it can be tuned before Apply.</summary>
        void ApplyLook(string name, PicAdjustments look)
        {
            if (!ReadyToAdjust()) return;
            sidebar.Tab = 1;
            sidebar.ShowAdjust(look);
            adjustName = name;
            RunAdjustPreview(sidebar.Adjust);
            Flash(name + ": tune the sliders if you like, then press Apply");
        }

        void ShowLevels()
        {
            if (!ReadyToAdjust()) return;
            var a = new PicAdjustments();
            using (var d = new LevelsDialog(ActiveHistogram(), lv => { var c = lv.ToCurve(); a.CurveR = a.CurveG = a.CurveB = lv.IsIdentity ? null : c; RunAdjustPreview(a); }))
            {
                adjustName = "Levels";
                if (d.ShowDialog(this) == DialogResult.OK) ApplyPendingAdjust(); else DropAdjust(true);
            }
        }

        void ShowCurves()
        {
            if (!ReadyToAdjust()) return;
            var a = new PicAdjustments();
            using (var d = new CurvesDialog(ActiveHistogram(), (r, g, b) => { a.CurveR = r; a.CurveG = g; a.CurveB = b; RunAdjustPreview(IsStraight(r) && IsStraight(g) && IsStraight(b) ? new PicAdjustments() : a); }))
            {
                adjustName = "Curves";
                if (d.ShowDialog(this) == DialogResult.OK) ApplyPendingAdjust(); else DropAdjust(true);
            }
        }

        static bool IsStraight(byte[] lut) { for (int i = 0; i < 256; i++) if (lut[i] != i) return false; return true; }

        // ==================================================================== size and shape

        bool ReadyForPicture()
        {
            if (current == null && edit == null) { SystemSounds(); return false; }
            EnsurePictureEdit();
            if (edit == null) return false;
            Prepare();
            return true;
        }

        void ShowResize()
        {
            if (!ReadyForPicture()) return;
            using (var d = new ResizeDialog(edit.Size))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.NewSize == edit.Size) return;
                Busy(() => edit.ResizeImage(d.NewSize.Width, d.NewSize.Height, d.Smooth));
            }
        }

        void ShowCanvasSize()
        {
            if (!ReadyForPicture()) return;
            using (var d = new CanvasDialog(edit.Size))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.NewSize == edit.Size) return;
                edit.SetCanvasSize(d.NewSize.Width, d.NewSize.Height, d.AnchorX, d.AnchorY);
            }
        }

        void ShowStraighten()
        {
            if (!ReadyForPicture()) return;
            // a small copy of the finished picture is enough for the preview
            var mips = edit.Compositor.Mips;
            var small = mips.Level(mips.LevelFor(900f / Math.Max(edit.Size.Width, edit.Size.Height)));
            using (var d = new StraightenDialog(small.ToBitmap()))
            {
                if (d.ShowDialog(this) != DialogResult.OK || Math.Abs(d.Angle) < 0.05) return;
                Busy(() => edit.RotateFree(d.Angle, d.CropToFit));
            }
        }

        void TrimEdges()
        {
            if (!ReadyForPicture()) return;
            if (!edit.TrimEdges()) Flash("There are no plain edges to trim");
        }

        void Busy(Action work)
        {
            var old = Cursor;
            Cursor = Cursors.WaitCursor;
            try { work(); }
            catch (OutOfMemoryException) { MessageBox.Show(this, "There isn't enough memory for that. Try a smaller size.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = old; }
        }

        // ==================================================================== saving

        /// <summary>Opens the layers of a layered picture (.srpic) for editing; null for any other picture.</summary>
        static EditSession OpenLayers(LoadedImage li)
        {
            if (li == null || !li.IsProject || li.CachePath == null) return null;
            try { return new EditSession(PicProject.Load(li.CachePath), li.SourcePath); }
            catch { return null; }   // falls back to the flat picture that is on screen
        }

        /// <summary>
        /// A picture being edited that came from a layered file is saved straight back into it (it is this app's own
        /// working file, like a video project). Returns false when that doesn't apply, or the save failed.
        /// </summary>
        bool SaveLayersInPlace()
        {
            if (mode != AppMode.PictureEdit && mode != AppMode.Pictures) return false;
            if (edit == null || !PicProject.IsProject(edit.SourcePath)) return false;
            try
            {
                Prepare();
                string file = edit.SourcePath;
                Busy(() => PicProject.Save(edit.State, file));
                edit.MarkSaved();
                Flash("Saved " + Path.GetFileName(file));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the picture:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        static int SaveFilterIndex(string ext, out string outExt)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".jpe": case ".jfif": outExt = ".jpg"; return 2;
                case ".webp": outExt = ".webp"; return 3;
                case ".bmp": case ".dib": outExt = ".bmp"; return 4;
                case ".gif": outExt = ".gif"; return 5;
                case ".tif": case ".tiff": outExt = ".tif"; return 6;
                case ".srpic": outExt = ".srpic"; return 7;
                default: outExt = ".png"; return 1;
            }
        }

        /// <summary>Asks where to save a copy of the picture, then saves it. With <paramref name="withLayers"/> the layered kind is offered first.</summary>
        bool SavePictureAs(bool withLayers)
        {
            string source = edit != null ? edit.SourcePath : (current != null ? current.SourcePath : null);
            if (source == null && edit == null) { SystemSounds(); return false; }
            if (edit != null) Prepare();
            if (source == null)
            {
                // a new picture (pasted or snipped) that has never been saved
                string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                source = Path.Combine(string.IsNullOrEmpty(pics) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : pics, (editName ?? "Picture") + ".png");
            }
            bool fromLayers = PicProject.IsProject(source);

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Save a copy";
                dlg.Filter = FileTypes.SaveFilter;
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                string dir = Path.GetDirectoryName(source);
                if (ImageCache.IsPicked(source))
                {
                    // picked up from a web page or another app: it only lives in the temporary folder
                    string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    dir = string.IsNullOrEmpty(pics) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : pics;
                }
                string name = Path.GetFileNameWithoutExtension(source);
                string ext = (Path.GetExtension(source) ?? "").ToLowerInvariant();
                // a kind the app can open but not write is saved as PNG
                string outExt;
                int filter = SaveFilterIndex(withLayers ? ".srpic" : ext, out outExt);
                dlg.FilterIndex = filter;
                dlg.DefaultExt = outExt.TrimStart('.');
                try { dlg.InitialDirectory = dir; } catch { }
                string suggestion;
                if (edit != null && edit.SourcePath == null || withLayers && !fromLayers || fromLayers && outExt == ".srpic") suggestion = name;
                else suggestion = name + " (edited)";
                bool sameAsSource = fromLayers && outExt == ".srpic";
                if (!sameAsSource)
                    for (int i = 2; File.Exists(Path.Combine(dir ?? "", suggestion + outExt)) && i < 1000; i++) suggestion = name + " (edited " + i + ")";
                dlg.FileName = suggestion + outExt;

                while (true)
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    bool original = edit != null && edit.SourcePath != null && SamePath(dlg.FileName, edit.SourcePath) || current != null && SamePath(dlg.FileName, current.SourcePath);
                    // the one file that may be saved over is the layered picture being edited: it is this app's working file
                    bool ownLayers = edit != null && PicProject.IsProject(dlg.FileName) && PicProject.IsProject(edit.SourcePath) && SamePath(dlg.FileName, edit.SourcePath);
                    if (original && !ownLayers)
                    {
                        MessageBox.Show(this, "Stills & Reels never overwrites the original file.\nPlease choose a different name or folder.",
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        dlg.FileName = suggestion + outExt;
                        continue;
                    }
                    break;
                }
                return SavePictureTo(dlg.FileName);
            }
        }

        bool SavePictureTo(string file)
        {
            try
            {
                if (PicProject.IsProject(file))
                {
                    if (edit != null)
                    {
                        Busy(() => PicProject.Save(edit.State, file));
                        edit.SourcePath = file;
                        edit.MarkSaved();
                        UpdateUi();
                    }
                    else
                    {
                        if (current == null || current.Surface == null) { SystemSounds(); return false; }
                        var one = new PicState(current.Surface.Size, new[] { PicLayer.Create("Background", current.Surface) });
                        Busy(() => PicProject.Save(one, file));
                    }
                    Flash("Saved " + Path.GetFileName(file) + " with its layers");
                    return true;
                }

                using (var bmp = CurrentPicture())
                {
                    if (bmp == null) { SystemSounds(); return false; }
                    SaveOptions o = null;
                    bool jpeg = ImageSaver.IsJpeg(file), webp = ImageSaver.IsWebp(file);
                    if (jpeg || webp)
                    {
                        // photo details can come along only from an original JPEG
                        string detailsFile = current != null && current.CachePath != null && ImageSaver.IsJpeg(current.SourcePath ?? "") &&
                                             (edit == null || SamePath(edit.SourcePath ?? "", current.SourcePath ?? "")) && JpegDetails.Has(current.CachePath) ? current.CachePath : null;
                        object gate = new object();
                        Func<int, long> estimate = null;
                        if (jpeg) estimate = q => { lock (gate) return PicCodecs.EncodeJpeg(bmp, q).LongLength; };
                        using (var od = new SaveOptionsDialog(webp, settings.GetInt(webp ? "WebpQuality" : "JpegQuality", webp ? 90 : 92), settings.GetBool("WebpLossless", false),
                                                              jpeg && detailsFile != null, settings.GetBool("KeepPhotoDetails", false), estimate))
                        {
                            var r = od.ShowDialog(this);
                            if (r != DialogResult.OK) { lock (gate) { } return false; }
                            o = new SaveOptions { Quality = od.Quality, Lossless = od.Lossless, DetailsFrom = od.KeepDetails ? detailsFile : null };
                            settings.Set(webp ? "WebpQuality" : "JpegQuality", od.Quality);
                            if (webp) settings.Set("WebpLossless", od.Lossless);
                            if (jpeg && detailsFile != null) settings.Set("KeepPhotoDetails", od.KeepDetails);
                        }
                        lock (gate) { }   // a size guess still running finishes before the picture is used again
                    }
                    Exception failed = null;
                    Busy(() => { try { ImageSaver.SaveWith(bmp, file, o); } catch (Exception ex) { failed = ex; } });
                    if (failed != null) throw failed;
                }
                if (edit != null) edit.MarkSaved();
                Flash("Saved " + Path.GetFileName(file));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the picture:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // ==================================================================== layers

        void ShowBlendMenu()
        {
            if (edit == null) return;
            var m = new ContextMenuStrip();
            foreach (PicBlend b in Enum.GetValues(typeof(PicBlend)))
            {
                var blend = b;
                var item = new ToolStripMenuItem(PicBlendOps.Name(b)) { Checked = edit.ActiveLayer.Blend == b };
                item.Click += (s, e) => { if (edit != null) edit.SetLayerBlend(edit.ActiveIndex, blend); };
                m.Items.Add(item);
            }
            m.Closed += (s, e) => BeginInvoke((MethodInvoker)m.Dispose);
            Ui.ShowMenuBelow(m, sidebar.BlendAnchor);
        }

        void RenameLayer()
        {
            if (edit == null) return;
            string name = PicDialog.AskText(this, "Rename layer", "Name for this layer", edit.ActiveLayer.Name);
            if (name != null && edit != null) edit.RenameLayer(edit.ActiveIndex, name);
        }

        // ==================================================================== keys

        /// <summary>Keys of the picture editor that aren't menu shortcuts. True when the key was used.</summary>
        bool PictureKey(Keys keyData)
        {
            if (mode != AppMode.PictureEdit || edit == null || IsMenuOpen() || menu.ContainsFocus) return false;
            if (canvas.IsEditingText) return false;
            switch (keyData)
            {
                case Keys.Control | Keys.A: OnPictureCommand("sel.all"); return true;
                case Keys.Control | Keys.Shift | Keys.A: OnPictureCommand("sel.none"); return true;
                case Keys.Control | Keys.Shift | Keys.I: OnPictureCommand("sel.invert"); return true;
                case Keys.Control | Keys.X: if (edit.Selection != null) { CopySelection(true); return true; } return false;
                case Keys.Control | Keys.D: if (canvas.SelectedObject != null) { canvas.DuplicateSelected(); return true; } return false;
                case Keys.Control | Keys.Alt | Keys.I: ShowResize(); return true;
                case Keys.Control | Keys.Alt | Keys.C: ShowCanvasSize(); return true;
                case Keys.Control | Keys.L: ShowLevels(); return true;
                case Keys.Control | Keys.Shift | Keys.L: AutoFix(); return true;
                case Keys.Enter: if (HasPendingAdjust && !canvas.IsTransformingLayer && tool != EditTool.Crop && canvas.SelectedObject == null) { ApplyPendingAdjust(); return true; } return false;
                case Keys.Escape: if (HasPendingAdjust && !canvas.IsTransformingLayer && tool != EditTool.Crop && canvas.SelectedObject == null && edit.Selection == null) { DropAdjust(true); return true; } return false;
                case Keys.OemOpenBrackets: NudgeSize(-1); return true;
                case Keys.OemCloseBrackets: NudgeSize(1); return true;
            }
            if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) != 0) return false;
            EditTool? t = null;
            switch (keyData)
            {
                case Keys.V: t = EditTool.Select; break;
                case Keys.M: t = EditTool.RectSelect; break;
                case Keys.L: t = EditTool.Lasso; break;
                case Keys.W: t = EditTool.Wand; break;
                case Keys.C: t = EditTool.Crop; break;
                case Keys.P: t = EditTool.Pen; break;
                case Keys.H: t = EditTool.Highlighter; break;
                case Keys.E: t = EditTool.Eraser; break;
                case Keys.G: t = EditTool.Fill; break;
                case Keys.I: t = EditTool.Picker; break;
                case Keys.A: t = EditTool.Arrow; break;
                case Keys.R: t = EditTool.Rect; break;
                case Keys.O: t = EditTool.Ellipse; break;
                case Keys.T: t = EditTool.Text; break;
            }
            if (t.HasValue) { SelectTool(t.Value); return true; }
            return false;
        }

        void NudgeSize(int by)
        {
            var key = ToolKey(tool);
            int size;
            if (!toolSizes.TryGetValue(key, out size)) return;
            int step = size >= 20 ? 4 : size >= 8 ? 2 : 1;
            toolSizes[key] = Math.Max(1, Math.Min(MaxSizeFor(tool), size + by * step));
            ApplyToolToCanvas();
            SyncSidebar();
        }

        // ==================================================================== menus

        void AddPictureMenus(ToolStripMenuItem file, ToolStripMenuItem editMenu, ToolStripMenuItem view)
        {
            miSelectAllPic = Item("Select &all", Keys.None, (s, e) => { EnsurePictureEdit(); OnPictureCommand("sel.all"); }, "Ctrl+A");
            miDeselect = Item("&Deselect", Keys.None, (s, e) => OnPictureCommand("sel.none"), "Ctrl+Shift+A");
            miInvertSel = Item("In&vert selection", Keys.None, (s, e) => OnPictureCommand("sel.invert"), "Ctrl+Shift+I");
            miCutSel = Item("Cu&t selection", Keys.None, (s, e) => CopySelection(true), "Ctrl+X");
            miDeleteSel = Item("Clear selected area", Keys.None, (s, e) => OnPictureCommand("sel.delete"), "Del");
            miCropSel = Item("Crop to selection", Keys.None, (s, e) => CropToSelection());
            miDuplicateObj = Item("Duplicate selected item", Keys.None, (s, e) => canvas.DuplicateSelected(), "Ctrl+D");
            int at = editMenu.DropDownItems.IndexOf(miPasteNew) + 1;
            foreach (var it in new ToolStripItem[] { new ToolStripSeparator(), miSelectAllPic, miDeselect, miInvertSel, miCutSel, miDeleteSel, miCropSel, miDuplicateObj })
                editMenu.DropDownItems.Insert(at++, it);

            miImage = new ToolStripMenuItem("&Picture");
            miAutoFix = Item("&Auto fix", Keys.None, (s, e) => AutoFix(), "Ctrl+Shift+L");
            miLevels = Item("&Levels…", Keys.None, (s, e) => ShowLevels(), "Ctrl+L");
            miCurves = Item("&Curves…", Keys.None, (s, e) => ShowCurves());
            miFilters = new ToolStripMenuItem("&Filters");
            foreach (var kv in PicAdjustments.Looks)
            {
                var look = kv;
                miFilters.DropDownItems.Add(new ToolStripMenuItem(look.Key, null, (s, e) => ApplyLook(look.Key, look.Value)));
            }
            miResize = Item("&Resize…", Keys.None, (s, e) => ShowResize(), "Ctrl+Alt+I");
            miCanvas = Item("Canvas &size…", Keys.None, (s, e) => ShowCanvasSize(), "Ctrl+Alt+C");
            miStraighten = Item("&Straighten…", Keys.None, (s, e) => ShowStraighten());
            miTrim = Item("&Trim edges", Keys.None, (s, e) => TrimEdges());
            miLayerMenu = new ToolStripMenuItem("La&yers");
            miLayerMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&New layer", Keys.None, (s, e) => { if (ReadyForPicture()) { sidebar.Tab = 2; OnPictureCommand("layer.add"); } }),
                Item("&Duplicate layer", Keys.None, (s, e) => OnPictureCommand("layer.dup")),
                Item("D&elete layer", Keys.None, (s, e) => OnPictureCommand("layer.delete")),
                new ToolStripSeparator(),
                Item("Merge do&wn", Keys.None, (s, e) => OnPictureCommand("layer.merge")),
                Item("&Flatten", Keys.None, (s, e) => OnPictureCommand("layer.flatten")),
                new ToolStripSeparator(),
                Item("&Save with layers…", Keys.None, (s, e) => { if (ReadyForPicture()) SavePictureAs(true); }),
            });
            miImage.DropDownItems.AddRange(new ToolStripItem[]
            {
                miAutoFix, miLevels, miCurves, miFilters, new ToolStripSeparator(),
                miResize, miCanvas, miStraighten, miTrim, new ToolStripSeparator(), miLayerMenu
            });
        }

        void UpdatePictureMenus(bool pictures)
        {
            bool has = pictures && (current != null || edit != null);
            bool editing = mode == AppMode.PictureEdit && edit != null;
            bool sel = editing && edit.Selection != null;
            miSelectAllPic.Visible = miDeselect.Visible = miInvertSel.Visible = miCutSel.Visible = miDeleteSel.Visible = miCropSel.Visible = miDuplicateObj.Visible = pictures;
            miSelectAllPic.Enabled = has;
            miDeselect.Enabled = miCutSel.Enabled = miDeleteSel.Enabled = miCropSel.Enabled = sel;
            miInvertSel.Enabled = editing;
            miDuplicateObj.Enabled = editing && canvas.SelectedObject != null;
            miImage.Visible = pictures;
            foreach (ToolStripItem it in miImage.DropDownItems) it.Enabled = has;
            foreach (ToolStripItem it in miLayerMenu.DropDownItems) it.Enabled = editing || (has && it.Text.Contains("New"));
        }
    }
}
