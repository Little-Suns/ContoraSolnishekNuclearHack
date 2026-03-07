/*
Ссылки:
TFlex.DOCs.Common.dll
*/


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TFlex.DOCs.Common;
using TFlex.DOCs.Model.FilePreview.CADService;
using TFlex.DOCs.Model.Access;
using TFlex.DOCs.Model.Macros;
using TFlex.DOCs.Model.Macros.ObjectModel;
using TFlex.DOCs.Model.References;
using TFlex.DOCs.Model.References.Files;
using TFlex.DOCs.Model.References.Users;

public class Macro : MacroProvider
{
    private const string FilesReferenceName = "Файлы";
    private const string UsersReferenceName = "Группы и пользователи";
    private const string StudentsRootFolderName = "Студенты";
    private const string AssignmentsRootFolderName = "Задания";
    private const string TeachersGroupName = "Преподаватели";
    private const string EditorAccessGroupName = "Редакторский";
    private const int DistributionAttemptCount = 32;
    private const bool ExportGrbAsPdf = true;

    private static readonly StringComparer FolderNameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Random RandomGenerator = new Random();

    private bool? isCadDocumentProviderActive;

    public Macro(MacroContext context) : base(context) { }

    public void RunWithCADExport()
    {
        if (IsCadDocumentProviderActive())
        {
            Сообщение("Информация", "CAD-документ провайдер активен. Файлы .grb будут экспортироваться в формате " + (ExportGrbAsPdf ? ".pdf" : ".tiff") + ".");
        }
        else
        {
            Сообщение("Информация", "CAD-документ провайдер не активен. Файлы .grb будут копироваться без изменений.");
        }

        ExecuteDistribution(true);
    }

    public override void Run()
    {
        ExecuteDistribution(false);
    }

    private void ExecuteDistribution(bool enableCadExport)
    {
        var fileReference = new FileReference(Context.Connection);
        fileReference.LoadSettings.AddGroup(fileReference.ParameterGroup);

        FolderObject assignmentsRootFolder = FindTopLevelFolderByName(fileReference, AssignmentsRootFolderName);
        if (assignmentsRootFolder == null)
        {
            Сообщение("Ошибка", $"Не найдена папка '{AssignmentsRootFolderName}' в корне справочника '{FilesReferenceName}'.");
            return;
        }

        FolderObject studentsRootFolder = FindTopLevelFolderByName(fileReference, StudentsRootFolderName);
        if (studentsRootFolder == null)
        {
            Сообщение("Ошибка", $"Не найдена папка '{StudentsRootFolderName}' в справочнике '{FilesReferenceName}'.");
            return;
        }

        List<AssignmentSet> assignmentSets = LoadAssignmentSets(assignmentsRootFolder);
        if (assignmentSets.Count == 0)
        {
            Сообщение("Ошибка", $"В папке '{AssignmentsRootFolderName}' не найдено ни одной подпапки с файлами заданий.");
            return;
        }

        if (!RestrictAssignmentsFolderAccess(assignmentsRootFolder))
            return;

        List<FolderObject> groupFolders = GetChildFolders(studentsRootFolder)
            .OrderBy(folder => folder.Name, FolderNameComparer)
            .ToList();

        if (groupFolders.Count == 0)
        {
            Сообщение("Ошибка", $"В папке '{StudentsRootFolderName}' не найдено ни одной папки группы.");
            return;
        }

        var reportLines = new List<string>();
        foreach (FolderObject groupFolder in groupFolders)
        {
            string groupReport = DistributeAssignmentsToGroup(groupFolder, assignmentSets, enableCadExport);
            if (!string.IsNullOrWhiteSpace(groupReport))
                reportLines.Add(groupReport);
        }

        if (reportLines.Count == 0)
        {
            Сообщение("Результат", "Не найдено папок студентов для распределения заданий.");
            return;
        }

        Сообщение("Результат", string.Join(Environment.NewLine + Environment.NewLine, reportLines));
    }

    private bool RestrictAssignmentsFolderAccess(FolderObject assignmentsRootFolder)
    {
        var userReference = new UserReference(Context.Connection);
        userReference.LoadSettings.AddGroup(userReference.ParameterGroup);

        ReferenceObject teachersGroup = FindGroupByName(userReference, TeachersGroupName, null);
        if (teachersGroup == null)
        {
            Сообщение("Ошибка", $"Не найдена группа '{TeachersGroupName}' в справочнике '{UsersReferenceName}'.");
            return false;
        }

        AccessGroup editorAccessGroup = AccessGroup.Find(Context.Connection, EditorAccessGroupName);
        if (editorAccessGroup == null)
        {
            Сообщение("Ошибка", $"Не найдена группа прав доступа '{EditorAccessGroupName}'.");
            return false;
        }

        try
        {
            var accessManager = AccessManager.GetReferenceObjectAccess(assignmentsRootFolder, default(AccessRightsLoadOptions));
            accessManager.SetInherit(false, false);
            accessManager.SetAccess(
                0,
                teachersGroup as UserReferenceObject,
                editorAccessGroup,
                null,
                null,
                accessManager.CommandType,
                accessManager.AccessTypeID,
                AccessDirection.Default,
                null);

            if (!accessManager.Save())
            {
                Сообщение("Ошибка", $"Не удалось сохранить права доступа для папки '{assignmentsRootFolder.Name}'.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Сообщение("Ошибка", $"Не удалось ограничить доступ к папке '{assignmentsRootFolder.Name}': {ex.Message}");
            return false;
        }
    }

    private string DistributeAssignmentsToGroup(FolderObject groupFolder, IReadOnlyList<AssignmentSet> assignmentSets, bool enableCadExport)
    {
        List<FolderObject> studentFolders = GetChildFolders(groupFolder)
            .Where(folder => !FolderNameComparer.Equals(folder.Name, AssignmentsRootFolderName))
            .OrderBy(folder => folder.Name, FolderNameComparer)
            .ToList();

        if (studentFolders.Count == 0)
            return string.Empty;

        var reportLines = new List<string>
        {
            $"Группа: {groupFolder.Name}"
        };

        Dictionary<string, List<int>> assignmentPlans = BuildDistributionPlans(studentFolders.Count, assignmentSets);

        for (int studentIndex = 0; studentIndex < studentFolders.Count; studentIndex++)
        {
            FolderObject studentFolder = studentFolders[studentIndex];
            FolderObject studentAssignmentsFolder = EnsureChildFolder(studentFolder, AssignmentsRootFolderName);
            if (studentAssignmentsFolder == null)
            {
                throw new InvalidOperationException($"Не удалось создать папку заданий для студента '{studentFolder.Name}'.");
            }

            var selectedNames = new List<string>();
            foreach (AssignmentSet assignmentSet in assignmentSets)
            {
                int sourceFileIndex = assignmentPlans[assignmentSet.WorkFolder.Name][studentIndex];
                FileObject sourceFile = assignmentSet.Files[sourceFileIndex];
                string targetFileName = BuildTargetFileName(assignmentSet.WorkFolder.Name, sourceFile, enableCadExport);

                CopyAssignmentFile(sourceFile, studentAssignmentsFolder, targetFileName, enableCadExport);
                selectedNames.Add(targetFileName);
            }

            reportLines.Add($"  {studentFolder.Name}: {string.Join(", ", selectedNames)}");
        }

        return string.Join(Environment.NewLine, reportLines);
    }

    private void CopyAssignmentFile(FileObject sourceFile, FolderObject studentAssignmentsFolder, string targetFileName, bool enableCadExport)
    {
        EnsureAssignmentTargetNameIsAvailable(studentAssignmentsFolder, targetFileName);

        string sourceExtension = Path.GetExtension(sourceFile?.Name ?? string.Empty);
        if (enableCadExport && FolderNameComparer.Equals(sourceExtension, ".grb") && IsCadDocumentProviderActive())
        {
            ExportCadAssignmentFile(sourceFile, studentAssignmentsFolder, targetFileName);
            return;
        }

        FileObject copiedFile = sourceFile.CreateCopy(targetFileName, studentAssignmentsFolder, null);
        if (copiedFile == null)
            throw new InvalidOperationException($"Не удалось создать копию файла '{sourceFile.Name}'.");

        copiedFile.EndChanges();
    }

    private string BuildTargetFileName(string workFolderName, FileObject sourceFile, bool enableCadExport)
    {
        string extension = GetAssignmentOutputExtension(sourceFile, enableCadExport);
        return workFolderName + extension;
    }

    private void ExportCadAssignmentFile(FileObject sourceFile, FolderObject studentAssignmentsFolder, string targetFileName)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "TFlexDocsMacroExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);

        string sourceFileName = sourceFile?.Name ?? "document.grb";
        string grbPath = Path.Combine(tempDirectoryPath, sourceFileName);
        string exportPath = Path.Combine(tempDirectoryPath, targetFileName);

        try
        {
            sourceFile.Export(grbPath, true);
            ExportCadDocument(grbPath, exportPath, sourceFileName);

            var fileReference = new FileReference(Context.Connection);
            FileType fileType = fileReference.GetFileType(targetFileName, true);
            if (fileType == null)
                throw new InvalidOperationException($"Не удалось определить тип файла для '{targetFileName}'.");

            FileObject importedFile = studentAssignmentsFolder.CreateFile(exportPath, string.Empty, targetFileName, fileType, null);
            if (importedFile == null)
                throw new InvalidOperationException($"Не удалось импортировать файл '{targetFileName}' в папку '{studentAssignmentsFolder.Name}'.");

            importedFile.EndChanges();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectoryPath))
                    Directory.Delete(tempDirectoryPath, true);
            }
            catch
            {
            }
        }
    }

    private void ExportCadDocument(string grbPath, string exportPath, string fileName)
    {
        CadDocumentProvider provider = CadDocumentProvider.Connect(Context.Connection, ".grb");
        if (provider == null || !provider.IsActive)
            throw new InvalidOperationException("Не удалось подключиться к CAD-системе для экспорта файлов .grb.");

        using (CadDocument document = provider.OpenDocument(grbPath, true))
        {
            if (document == null)
                throw new InvalidOperationException($"Файл '{fileName}' не может быть открыт для экспорта.");

            var exportContext = new ExportContext(exportPath);
            string exportedFilePath = document.Export(exportContext);

            if (string.IsNullOrWhiteSpace(exportedFilePath))
                throw new InvalidOperationException($"Файл '{fileName}' не может быть экспортирован.");

            document.Close(false);
        }
    }

    private string GetAssignmentOutputExtension(FileObject sourceFile, bool enableCadExport)
    {
        string extension = Path.GetExtension(sourceFile?.Name ?? string.Empty);
        if (FolderNameComparer.Equals(extension, ".grb"))
            return enableCadExport && IsCadDocumentProviderActive() ? (ExportGrbAsPdf ? ".pdf" : ".tiff") : extension;

        return extension;
    }

    private bool IsCadDocumentProviderActive()
    {
        if (isCadDocumentProviderActive.HasValue)
            return isCadDocumentProviderActive.Value;

        try
        {
            CadDocumentProvider provider = CadDocumentProvider.Connect(Context.Connection, ".grb");
            isCadDocumentProviderActive = provider != null && provider.IsActive;
        }
        catch
        {
            isCadDocumentProviderActive = false;
        }

        return isCadDocumentProviderActive.Value;
    }

    private void EnsureAssignmentTargetNameIsAvailable(FolderObject targetFolder, string targetFileName)
    {
        FileObject existingFile = FindChildFile(targetFolder, targetFileName);
        if (existingFile == null)
            return;

        try
        {
            existingFile.Delete();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось удалить существующий файл '{targetFileName}' в папке '{targetFolder.Name}'. Из-за этого T-FLEX создаст копию с меткой времени.",
                ex);
        }

        if (FindChildFile(targetFolder, targetFileName) != null)
        {
            throw new InvalidOperationException(
                $"Файл '{targetFileName}' остался в папке '{targetFolder.Name}' после удаления. Чтобы не создавать копию с меткой времени, операция остановлена.");
        }
    }

    private Dictionary<string, List<int>> BuildDistributionPlans(int studentCount, IReadOnlyList<AssignmentSet> assignmentSets)
    {
        List<AssignmentSet> planningSets = assignmentSets
            .OrderByDescending(set => set.Files.Count)
            .ThenBy(set => set.WorkFolder.Name, FolderNameComparer)
            .ToList();

        Dictionary<string, List<int>> bestPlans = null;
        int bestDuplicatePairCount = int.MaxValue;

        for (int attempt = 0; attempt < DistributionAttemptCount; attempt++)
        {
            string[] currentSignatures = Enumerable.Repeat(string.Empty, studentCount).ToArray();
            var currentPlans = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (AssignmentSet assignmentSet in planningSets)
            {
                int[] remainingCounts = BuildFileUsageCounts(studentCount, assignmentSet.Files.Count);
                List<int> studentOrder = Enumerable.Range(0, studentCount)
                    .OrderBy(_ => RandomGenerator.Next())
                    .ToList();
                List<int> plan = Enumerable.Repeat(-1, studentCount).ToList();
                var nextSignatures = new string[studentCount];
                var signatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (int studentIndex in studentOrder)
                {
                    int selectedFileIndex = SelectFileIndexForStudent(currentSignatures[studentIndex], remainingCounts, signatureCounts);
                    string nextSignature = AppendSignature(currentSignatures[studentIndex], selectedFileIndex);

                    remainingCounts[selectedFileIndex]--;
                    plan[studentIndex] = selectedFileIndex;
                    nextSignatures[studentIndex] = nextSignature;
                    signatureCounts[nextSignature] = GetDictionaryValue(signatureCounts, nextSignature) + 1;
                }

                currentPlans[assignmentSet.WorkFolder.Name] = plan;
                currentSignatures = nextSignatures;
            }

            int duplicatePairCount = CountDuplicateSignaturePairs(currentSignatures);
            if (duplicatePairCount < bestDuplicatePairCount)
            {
                bestDuplicatePairCount = duplicatePairCount;
                bestPlans = currentPlans;
            }

            if (bestDuplicatePairCount == 0)
                break;
        }

        return bestPlans ?? new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    }

    private int[] BuildFileUsageCounts(int studentCount, int fileCount)
    {
        if (studentCount <= 0 || fileCount <= 0)
            return Array.Empty<int>();

        List<int> shuffledIndices = Enumerable.Range(0, fileCount)
            .OrderBy(_ => RandomGenerator.Next())
            .ToList();

        int baseCount = studentCount / fileCount;
        int remainder = studentCount % fileCount;
        int[] counts = Enumerable.Repeat(baseCount, fileCount).ToArray();

        for (int i = 0; i < remainder; i++)
        {
            counts[shuffledIndices[i]]++;
        }

        return counts;
    }

    private int SelectFileIndexForStudent(string currentSignature, int[] remainingCounts, Dictionary<string, int> signatureCounts)
    {
        int bestFileIndex = -1;
        int bestDuplicateCount = int.MaxValue;

        for (int fileIndex = 0; fileIndex < remainingCounts.Length; fileIndex++)
        {
            if (remainingCounts[fileIndex] <= 0)
                continue;

            string nextSignature = AppendSignature(currentSignature, fileIndex);
            int duplicateCount = GetDictionaryValue(signatureCounts, nextSignature);
            if (duplicateCount < bestDuplicateCount)
            {
                bestDuplicateCount = duplicateCount;
                bestFileIndex = fileIndex;
                continue;
            }

            if (duplicateCount == bestDuplicateCount && RandomGenerator.Next(2) == 0)
            {
                bestFileIndex = fileIndex;
            }
        }

        if (bestFileIndex >= 0)
            return bestFileIndex;

        throw new InvalidOperationException("Не удалось подобрать файл для распределения задания.");
    }

    private int CountDuplicateSignaturePairs(IEnumerable<string> signatures) =>
        signatures
            .GroupBy(signature => signature, StringComparer.Ordinal)
            .Sum(group => group.Count() * (group.Count() - 1) / 2);

    private int GetDictionaryValue(Dictionary<string, int> source, string key) =>
        source.TryGetValue(key, out int value) ? value : 0;

    private string AppendSignature(string currentSignature, int fileIndex) =>
        string.IsNullOrEmpty(currentSignature)
            ? fileIndex.ToString()
            : $"{currentSignature}:{fileIndex}";

    private List<AssignmentSet> LoadAssignmentSets(FolderObject assignmentsRootFolder)
    {
        var assignmentSets = new List<AssignmentSet>();

        foreach (FolderObject workFolder in GetChildFolders(assignmentsRootFolder).OrderBy(folder => folder.Name, FolderNameComparer))
        {
            List<FileObject> files = GetChildFiles(workFolder)
                .Where(IsSupportedAssignmentFile)
                .OrderBy(file => file.Name, FolderNameComparer)
                .ToList();

            if (files.Count == 0)
                continue;

            assignmentSets.Add(new AssignmentSet
            {
                WorkFolder = workFolder,
                Files = files
            });
        }

        return assignmentSets;
    }

    private bool IsSupportedAssignmentFile(FileObject file)
    {
        string extension = Path.GetExtension(file?.Name ?? string.Empty);
        return FolderNameComparer.Equals(extension, ".grb")
            || FolderNameComparer.Equals(extension, ".pdf")
            || FolderNameComparer.Equals(extension, ".tif")
            || FolderNameComparer.Equals(extension, ".tiff");
    }

    private FolderObject FindTopLevelFolderByName(FileReference fileReference, string folderName)
    {
        if (fileReference == null || string.IsNullOrWhiteSpace(folderName))
            return null;

        fileReference.Objects.Load(fileReference.LoadSettings, 0, 0);

        var matchedFolders = new List<FolderMatch>();
        foreach (ReferenceObject rootObject in fileReference.Objects)
        {
            CollectFolderMatches(rootObject, folderName, 0, matchedFolders);
        }

        return matchedFolders
            .OrderBy(match => match.Depth)
            .ThenBy(match => match.Path, FolderNameComparer)
            .Select(match => match.Folder)
            .FirstOrDefault();
    }

    private void CollectFolderMatches(ReferenceObject referenceObject, string folderName, int depth, List<FolderMatch> matches)
    {
        if (referenceObject == null)
            return;

        if (referenceObject is FolderObject folder)
        {
            folder.Load(false);
            if (FolderNameComparer.Equals(folder.Name, folderName))
            {
                matches.Add(new FolderMatch
                {
                    Folder = folder,
                    Depth = depth,
                    Path = BuildFolderPath(folder)
                });
            }
        }

        var children = referenceObject.Children;
        if (children == null)
            return;

        foreach (ReferenceObject child in children)
        {
            CollectFolderMatches(child, folderName, depth + 1, matches);
        }
    }

    private string BuildFolderPath(FolderObject folder)
    {
        var parts = new List<string>();
        ReferenceObject current = folder;

        while (current != null)
        {
            parts.Add((current as FolderObject)?.Name ?? current.ToString() ?? string.Empty);
            current = current.Parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private List<FolderObject> GetChildFolders(FolderObject parentFolder)
    {
        if (parentFolder == null)
            return new List<FolderObject>();

        parentFolder.Load(false);
        var children = parentFolder.Children;
        if (children == null)
            return new List<FolderObject>();

        return children.OfType<FolderObject>().ToList();
    }

    private List<FileObject> GetChildFiles(FolderObject parentFolder)
    {
        if (parentFolder == null)
            return new List<FileObject>();

        parentFolder.Load(false);
        var children = parentFolder.Children;
        if (children == null)
            return new List<FileObject>();

        return children.OfType<FileObject>().Where(file => file.IsFile).ToList();
    }

    private FolderObject EnsureChildFolder(FolderObject parentFolder, string folderName)
    {
        if (parentFolder == null || string.IsNullOrWhiteSpace(folderName))
            return null;

        parentFolder.Load(false);

        FolderObject existingFolder = GetChildFolders(parentFolder)
            .FirstOrDefault(folder => FolderNameComparer.Equals(folder.Name, folderName));
        if (existingFolder != null)
            return existingFolder;

        return parentFolder.CreateFolder(string.Empty, folderName, null);
    }

    private FileObject FindChildFile(FolderObject parentFolder, string fileName) =>
        GetChildFiles(parentFolder)
            .FirstOrDefault(file => FolderNameComparer.Equals(file.Name, fileName));

    private ReferenceObject FindGroupByName(UserReference userReference, string name, TFlex.DOCs.Model.Structure.ParameterInfo nameParam)
    {
        if (userReference == null || string.IsNullOrWhiteSpace(name))
            return null;

        foreach (ReferenceObject rootGroup in userReference.GetAllUsersGroup())
        {
            ReferenceObject foundGroup = FindGroupRecursive(rootGroup, name, nameParam);
            if (foundGroup != null)
                return foundGroup;
        }

        return null;
    }

    private ReferenceObject FindGroupRecursive(ReferenceObject currentGroup, string name, TFlex.DOCs.Model.Structure.ParameterInfo nameParam)
    {
        if (currentGroup == null)
            return null;

        if (string.Equals(GetObjectDisplayName(currentGroup, nameParam), name, StringComparison.OrdinalIgnoreCase))
            return currentGroup;

        var children = currentGroup.Children;
        if (children == null)
            return null;

        foreach (ReferenceObject child in children)
        {
            ReferenceObject foundGroup = FindGroupRecursive(child, name, nameParam);
            if (foundGroup != null)
                return foundGroup;
        }

        return null;
    }

    private string GetObjectDisplayName(ReferenceObject referenceObject, TFlex.DOCs.Model.Structure.ParameterInfo nameParam)
    {
        if (referenceObject == null)
            return string.Empty;

        if (nameParam != null)
        {
            try
            {
                string value = referenceObject[nameParam].Value?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
            }
        }

        return referenceObject.ToString() ?? string.Empty;
    }

    private class AssignmentSet
    {
        public FolderObject WorkFolder { get; set; }
        public List<FileObject> Files { get; set; }
    }

    private class FolderMatch
    {
        public FolderObject Folder { get; set; }
        public int Depth { get; set; }
        public string Path { get; set; }
    }
}