﻿using PKHeX.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AutoLegalityMod
{
    public static class AutomaticLegality
    {
        static AutomaticLegality()
        {
            // Make a blank MGDB directory and initialize trainerdata
            if (!Directory.Exists(MGDatabasePath))
                Directory.CreateDirectory(MGDatabasePath);
            if (TrainerSettings.CheckMode() != AutoModMode.Game)
                Trainer = LoadTrainerData();
        }

        // TODO: Check for Auto Legality Mod Updates
        public static ISaveFileProvider SaveFileEditor { private get; set; }
        public static IPKMView PKMEditor { private get; set; }
        private static SaveFile SAV => API.SAV;
        private static SimpleTrainerInfo Trainer;

        /// <summary>
        /// Global Variables for Auto Legality Mod
        /// </summary>
        private static readonly string MGDatabasePath = Path.Combine(Directory.GetCurrentDirectory(), "mgdb");

        public static SimpleTrainerInfo GetTrainerData(this PKM illegalPK)
        {
            return new SimpleTrainerInfo
            {
                TID = illegalPK.TID,
                SID = illegalPK.SID,
                OT = illegalPK.OT_Name,
                Gender = illegalPK.OT_Gender,
            };
        }

        /// <summary>
        /// Checks whether a paste is a showdown team backup
        /// </summary>
        /// <param name="paste">paste to check</param>
        /// <returns>Returns bool</returns>
        public static bool IsTeamBackup(string paste) => paste.StartsWith("===");

        /// <summary>
        /// Imports <see cref="ShowdownSet"/> list(s) originating from a concatenated list.
        /// </summary>
        public static void ImportModded(string source)
        {
            var Sets = ShowdownSets(source, out Dictionary<int, string[]> TeamData);
            if (TeamData != null)
                WinFormsUtil.Alert(TeamDataAlert(TeamData));

            ImportModded(Sets);
        }

        /// <summary>
        /// Imports <see cref="ShowdownSet"/> list(s).
        /// </summary>
        public static void ImportModded(IEnumerable<string> sets)
        {
            var entries = sets.Select(z => new ShowdownSet(z)).ToList();
            ImportModded(entries);
        }

        public static void ImportModded(IReadOnlyList<ShowdownSet> Sets)
        {
            var timer = Stopwatch.StartNew();
            // Import Showdown Sets and alert user of any messages intended
            bool replace = (Control.ModifierKeys & Keys.Control) != 0;
            ImportSets(Sets, replace, out string message);

            // Debug Statements
            timer.Stop();
            TimeSpan timespan = timer.Elapsed;
            Debug.WriteLine($"Time to complete function: {timespan.Minutes:00} minutes {timespan.Seconds:00} seconds {timespan.Milliseconds / 10:00} milliseconds");

            if (!string.IsNullOrWhiteSpace(message))
                WinFormsUtil.Alert(message);
        }

        /// <summary>
        /// Loads the trainerdata variables into the global variables for AutoLegalityMod
        /// </summary>
        /// <param name="legal">Optional legal PKM for loading trainerdata on a per game basis</param>
        private static SimpleTrainerInfo LoadTrainerData(PKM legal = null)
        {
            bool checkPerGame = (TrainerSettings.CheckMode() == AutoModMode.Save);
            // If mode is not set as game: (auto or save)
            var tdataVals = !checkPerGame || legal == null
                ? TrainerSettings.ParseTrainerJSON(SAV)
                : TrainerSettings.ParseTrainerJSON(SAV, legal.Version);

            var trainer = GetTrainer(tdataVals);
            if (legal != null)
                trainer.SID = legal.VC ? 0 : trainer.SID;

            return trainer;
        }

        private static SimpleTrainerInfo GetTrainer(string[] tdataVals)
        {
            var trainer = new SimpleTrainerInfo();
            trainer.TID = Convert.ToInt32(tdataVals[0]);
            trainer.SID = Convert.ToInt32(tdataVals[1]);

            trainer.OT = tdataVals[2];
            if (trainer.OT == "PKHeX")
                trainer.OT = "Archit(TCD)"; // Avoids secondary handler error
            trainer.Gender = tdataVals[3] == "F" || tdataVals[3] == "Female" ? 1 : 0;

            // Load Trainer location details; check first if english string name
            // if not, try to check if they're stored as integers.
            trainer.Country = TrainerSettings.GetCountryID(tdataVals[4]);
            trainer.SubRegion = TrainerSettings.GetSubRegionID(tdataVals[5], trainer.Country);
            trainer.ConsoleRegion = TrainerSettings.GetConsoleRegionID(tdataVals[6]);

            if (trainer.Country < 0 && int.TryParse(tdataVals[4], out var c))
                trainer.Country = c;
            if (trainer.SubRegion < 0 && int.TryParse(tdataVals[5], out var s))
                trainer.SubRegion = s;
            if (trainer.ConsoleRegion < 0 && int.TryParse(tdataVals[6], out var x))
                trainer.ConsoleRegion = x;
            return trainer;
        }

        /// <summary>
        /// Function that generates legal PKM objects from ShowdownSets and views them/sets them in boxes
        /// </summary>
        /// <param name="sets">A list of ShowdownSet(s) that need to be genned</param>
        /// <param name="replace">A boolean that determines if current pokemon will be replaced or not</param>
        /// <param name="message">Output message to be displayed for the user</param>
        /// <param name="allowAPI">Use of generators before bruteforcing</param>
        private static void ImportSets(IReadOnlyList<ShowdownSet> sets, bool replace, out string message, bool allowAPI = true)
        {
            message = string.Empty;
            Debug.WriteLine("Commencing Import");
            if (sets.Count == 1)
            {
                var set = sets[0];
                if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Import this set?", sets[0].Text))
                    return;
                if (set.InvalidLines.Count > 0)
                    WinFormsUtil.Alert("Invalid lines detected:", string.Join(Environment.NewLine, set.InvalidLines));

                PKM legal = GetLegalFromSet(set, allowAPI, out var _);
                PKMEditor.PopulateFields(legal);
                Debug.WriteLine("Set Genning Complete");
                return;
            }

            var BoxData = SAV.BoxData;
            int start = SaveFileEditor.CurrentBox * SAV.BoxSlotCount;
            if (!ImportToExisting(sets, BoxData, start, replace, allowAPI, out message))
                return;

            SAV.BoxData = BoxData;
            SaveFileEditor.ReloadSlots();
        }

        private static bool ImportToExisting(IReadOnlyList<ShowdownSet> sets, IList<PKM> BoxData, int start, bool replace, bool allowAPI, out string message)
        {
            message = string.Empty;
            var emptySlots = replace
                ? Enumerable.Range(0, sets.Count).ToList()
                : FindAllEmptySlots(BoxData, SaveFileEditor.CurrentBox);

            if (emptySlots.Count < sets.Count && sets.Count != 1)
            {
                message = "Not enough space in the box.";
                return false;
            }

            int apiCounter = 0;
            var invalidAPISets = new List<ShowdownSet>();
            for (int i = 0; i < sets.Count; i++)
            {
                ShowdownSet Set = sets[i];
                if (Set.InvalidLines.Count > 0)
                    WinFormsUtil.Alert("Invalid lines detected:", string.Join(Environment.NewLine, Set.InvalidLines));

                PKM legal = GetLegalFromSet(Set, allowAPI, out var msg);
                switch (msg)
                {
                    case LegalizationResult.API_Valid:
                        apiCounter++;
                        break;
                    case LegalizationResult.API_Invalid:
                        invalidAPISets.Add(Set);
                        break;
                }

                BoxData[start + emptySlots[i]] = legal;
            }

            var total = invalidAPISets.Count + apiCounter;
            Debug.WriteLine($"API Genned Sets: {apiCounter}/{total}, {invalidAPISets.Count} were not.");
            foreach (ShowdownSet i in invalidAPISets)
                Debug.WriteLine(i.Text);
            return true;
        }

        private enum LegalizationResult
        {
            Other,
            API_Invalid,
            API_Valid,
        }

        private static PKM GetLegalFromSet(ShowdownSet Set, bool allowAPI, out LegalizationResult message)
        {
            PKM roughPKM = SAV.BlankPKM;
            roughPKM.ApplySetDetails(Set);
            roughPKM.Version = (int)GameVersion.MN; // Avoid the blank version glitch
            if (allowAPI && TryAPIConvert(Set, roughPKM, out PKM pkm))
            {
                message = LegalizationResult.API_Valid;
                return pkm;
            }
            message = LegalizationResult.API_Invalid;
            return GetBruteForcedLegalMon(Set, roughPKM);
        }

        private static bool TryAPIConvert(ShowdownSet Set, PKM roughPKM, out PKM pkm)
        {
            try
            {
                pkm = API.APILegality(roughPKM, Set, out bool satisfied);
                if (!satisfied)
                    return false;

                pkm.SetTrainerData();
                return true;
            }
            catch
            {
                pkm = null;
                return false;
            }
        }

        private static PKM GetBruteForcedLegalMon(ShowdownSet Set, PKM roughPKM)
        {
            BruteForce b = new BruteForce { SAV = SAV };
            bool resetForm = Set.Form != null && (Set.Form.Contains("Mega") || Set.Form == "Primal" || Set.Form == "Busted");
            var legal = b.ApplyDetails(roughPKM, Set, resetForm, Trainer);
            legal.SetTrainerData();
            return legal;
        }

        /// <summary>
        /// Set trainer data for a legal PKM
        /// </summary>
        /// <param name="legal">Legal PKM for setting the data</param>
        /// <returns>PKM with the necessary values modified to reflect trainerdata changes</returns>
        private static void SetTrainerData(this PKM legal)
        {
            var trainer = LoadTrainerData(legal);
            legal.SetTrainerData(trainer, true);
            legal.ConsoleRegion = trainer.ConsoleRegion;
            legal.Country = trainer.Country;
            legal.Region = trainer.SubRegion;
        }

        /// <summary>
        /// Method to find all empty slots in a current box
        /// </summary>
        /// <param name="BoxData">Box Data of the SAV file</param>
        /// <param name="CurrentBox">Index of the current box</param>
        /// <returns>A list of all indices in the current box that are empty</returns>
        private static List<int> FindAllEmptySlots(IList<PKM> BoxData, int CurrentBox)
        {
            var emptySlots = new List<int>();
            int BoxCount = SAV.BoxSlotCount;
            for (int i = 0; i < BoxCount; i++)
            {
                if (BoxData[(CurrentBox * BoxCount) + i].Species < 1)
                    emptySlots.Add(i);
            }
            return emptySlots;
        }

        /// <summary>
        /// A method to get a list of ShowdownSet(s) from a string paste
        /// Needs to be extended to hold several teams
        /// </summary>
        /// <param name="paste"></param>
        /// <param name="TeamData"></param>
        private static List<ShowdownSet> ShowdownSets(string paste, out Dictionary<int, string[]> TeamData)
        {
            TeamData = null;
            paste = paste.Trim(); // Remove White Spaces
            if (IsTeamBackup(paste))
                TeamData = GenerateTeamData(paste, out paste);
            string[] lines = paste.Split(new[] { "\n" }, StringSplitOptions.None);
            return ShowdownSet.GetShowdownSets(lines).ToList();
        }

        /// <summary>
        /// Method to generate team data based on the given paste if applicable.
        /// </summary>
        /// <param name="paste">input paste</param>
        /// <param name="modified">modified paste for normal importing</param>
        /// <returns>null or dictionary with the teamdata</returns>
        private static Dictionary<int, string[]> GenerateTeamData(string paste, out string modified)
        {
            string[] IndividualTeams = Regex.Split(paste, @"={3} \[.+\] .+ ={3}").Select(team => team.Trim()).ToArray();
            Dictionary<int, string[]> TeamData = new Dictionary<int, string[]>();
            modified = string.Join(Environment.NewLine + Environment.NewLine, IndividualTeams);
            Regex title = new Regex(@"={3} \[(?<format>.+)\] (?<teamname>.+) ={3}");
            MatchCollection titlematches = title.Matches(paste);
            for (int i = 0; i < titlematches.Count; i++)
            {
                TeamData[i] = new[] { titlematches[i].Groups["format"].Value, titlematches[i].Groups["teamname"].Value };
            }
            return TeamData.Count == 0 ? null : TeamData;
        }

        /// <summary>
        /// Convert Team Data into an alert for the main function
        /// </summary>
        /// <param name="TeamData">Dictionary with format as key and team name as value</param>
        private static string TeamDataAlert(Dictionary<int, string[]> TeamData)
        {
            string alert = "Generating the following teams:" + Environment.NewLine + Environment.NewLine;
            var lines = TeamData.Select(z => $"Format: {z.Value[0]}, Team Name: {z.Value[1]}");
            return alert + string.Join(Environment.NewLine, lines);
        }
    }
}
