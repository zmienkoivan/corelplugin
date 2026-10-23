using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutTab
    {
        private sealed class ProjectSnapshot
        {
            internal object Document;
            internal string Json;
            internal bool Written;
        }
        private readonly Dictionary<string, ProjectSnapshot> _projectSnapshots = new Dictionary<string, ProjectSnapshot>();
        private Draft _defaultDraft, _legacyDraft;
        private bool _legacyConsidered;
        private string _savedProjectDraft, _savedDefaults, _lastProjectError;
        private string _legacyDraftJson;
        private DateTime _lastProjectErrorAt;

        private static JavaScriptSerializer ProjectJson() => new JavaScriptSerializer { MaxJsonLength = 16777216 };
        private static string SerializeDraft(Draft draft) => ProjectJson().Serialize(draft);
        private static Draft ParseDraft(string json)
        {
            var draft = ProjectJson().Deserialize<Draft>(json);
            if (draft == null || (draft.Players?.Count ?? 0) > 10000 || (draft.Templates?.Count ?? 0) > 2000 ||
                (draft.Players?.Any(row => row == null) ?? false) || (draft.Templates?.Any(row => row == null) ?? false))
                throw new InvalidOperationException("Повреждены сохранённые данные формы в CDR.");
            return draft;
        }
        private static Draft Defaults(Draft draft)
        {
            var copy = ParseDraft(SerializeDraft(draft));
            copy.ProjectStorageVersion = 2;
            copy.Templates = new List<SportLayoutTemplate>(); copy.Players = new List<SportLayoutPlayer>();
            copy.ModelPath = copy.FrontArtworkPath = copy.BackArtworkPath = copy.LeftSleeveArtworkPath =
                copy.RightSleeveArtworkPath = copy.NeckArtworkPath = copy.PrintFolder = copy.CutFolder = "";
            copy.PatternPath = null; copy.WorkflowTab = 0;
            return copy;
        }
        private void LoadDraft()
        {
            _defaultDraft = Defaults(CaptureDraft());
            try
            {
                if (File.Exists(DraftPath))
                {
                    string json = File.ReadAllText(DraftPath, Encoding.UTF8);
                    var old = ParseDraft(json);
                    _defaultDraft = Defaults(old);
                    if (old.ProjectStorageVersion < 2) { _legacyDraft = old; _legacyDraftJson = json; }
                    else _savedDefaults = SerializeDraft(_defaultDraft);
                }
            }
            catch (Exception error) { Log.Error("Could not read sport defaults", error); }
            ApplyDraft(ParseDraft(SerializeDraft(_defaultDraft)));
        }
        private void BindProjectData(object document)
        {
            if (_switchingPlayers || _loading || SportProjectPlayers.SameDocument(document, _projectDocument)) return;
            string key = ProjectDocumentKey(document);
            _switchingPlayers = true;
            bool loading = _loading;
            try
            {
                _loading = true;
                if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
                    !_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
                    throw new InvalidOperationException("Закончите редактирование таблицы перед сменой проекта.");
                // Persist explicitly to the previous document, even though Corel already activated another tab.
                try { PersistProjectData(); }
                catch (Exception error) { ReportProjectError("Данные прежнего файла остались в памяти этой сессии", error); }
                string json = null;
                bool written = false;
                if (document != null)
                {
                    if (_projectSnapshots.TryGetValue(key, out ProjectSnapshot cached) &&
                        SportProjectPlayers.SameDocument(document, cached.Document))
                    { json = cached.Json; written = cached.Written; }
                    else { json = SportProjectState.Read(document); written = json != null; }
                }
                var incoming = json == null ? ParseDraft(SerializeDraft(_defaultDraft)) : ParseDraft(json);
                if (json != null && incoming.ProjectStorageVersion != 2)
                    throw new InvalidOperationException("Версия данных формы в CDR не поддерживается этим плагином.");
                bool migrate = false;
                if (document != null && json == null)
                {
                    if (!_legacyConsidered && _legacyDraft != null)
                    { incoming = ParseDraft(SerializeDraft(_legacyDraft)); migrate = true; }
                    var roster = SportProjectPlayers.Read(document);
                    if (roster != null) { incoming.Players = roster.Players; migrate = true; }
                }
                if (document != null) _legacyConsidered = true;
                ApplyDraft(incoming);
                _projectDocument = document;
                _workflowDocument = _workflowSession = _workflowId = _savedWorkflow = null;
                _stepInputs = null;
                Array.Clear(_completedSteps, 0, _completedSteps.Length);
                Array.Clear(_stepDetails, 0, _stepDetails.Length);
                _savedProjectDraft = written ? SerializeDraft(CaptureDraft()) : null;
                _savedProjectPlayers = null;
                if (document != null)
                {
                    _projectSnapshots[key] = new ProjectSnapshot { Document = document,
                        Json = SerializeDraft(CaptureDraft()), Written = written };
                    if (migrate) PersistProjectData();
                }
                PruneProjectSnapshots();
                Log.Info("Sport project: document switched, players=" + _players.Count + ", templates=" + _templates.Count);
            }
            finally { _loading = loading; _switchingPlayers = false; }
        }
        private static string ProjectDocumentKey(object document) => SportProjectPlayers.DocumentKey(document);
        private void PersistProjectData()
        {
            if (_projectDocument == null) return;
            var draft = CaptureDraft();
            string json = SerializeDraft(draft), key = ProjectDocumentKey(_projectDocument);
            _projectSnapshots[key] = new ProjectSnapshot { Document = _projectDocument, Json = json,
                Written = json == _savedProjectDraft };
            if (json != _savedProjectDraft)
            {
                SportProjectState.Write(_projectDocument, json);
                _savedProjectDraft = json; _projectSnapshots[key].Written = true;
            }
            // Retain compatibility with the previous roster-only storage.
            string players = SportProjectPlayers.Json(_players);
            if (players != _savedProjectPlayers)
            {
                try { SportProjectPlayers.Write(_projectDocument, players); _savedProjectPlayers = players; }
                catch (Exception error) { Log.Error("Could not update legacy sport roster", error); }
            }
            var defaults = Defaults(draft);
            string defaultsJson = SerializeDraft(defaults);
            if (defaultsJson != _savedDefaults)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DraftPath));
                string backup = Path.Combine(Path.GetDirectoryName(DraftPath), "sport-layout-draft.legacy.json");
                if (_legacyDraftJson != null && !File.Exists(backup))
                    File.WriteAllText(backup, _legacyDraftJson, new UTF8Encoding(false));
                File.WriteAllText(DraftPath, defaultsJson, new UTF8Encoding(false));
                _savedDefaults = defaultsJson; _defaultDraft = defaults;
            }
        }
        private void PruneProjectSnapshots()
        {
            try
            {
                var open = new List<object>();
                foreach (object document in CorelApp.Get().Documents) open.Add(document);
                foreach (string key in _projectSnapshots.Keys.ToArray())
                    if (!open.Any(document => SportProjectPlayers.SameDocument(document, _projectSnapshots[key].Document)))
                        _projectSnapshots.Remove(key);
            }
            catch { /* A native modal transition can temporarily reject document enumeration. */ }
        }
        private void GuardProjectEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (_busy || _switchingPlayers) { e.Cancel = true; return; }
            try
            {
                if (SportProjectPlayers.SameDocument((object)CorelApp.Get().ActiveDocument, _projectDocument)) return;
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(RefreshWorkflowDocument), DispatcherPriority.Background);
            }
            catch (Exception error) { e.Cancel = true; ReportProjectError("Не удалось определить текущий CDR", error); }
        }
        private void TemplateChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_loading) return;
            RefreshSizes(); SaveDraft();
        }
        private void ReportProjectError(string text, Exception error)
        {
            string signature = text + ": " + error.GetBaseException().Message;
            if (signature == _lastProjectError && DateTime.UtcNow - _lastProjectErrorAt < TimeSpan.FromSeconds(20)) return;
            _lastProjectError = signature; _lastProjectErrorAt = DateTime.UtcNow;
            Log.Error(text, error); _status(signature, true);
        }
    }
}
