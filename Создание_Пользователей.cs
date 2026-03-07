/*
Ссылки:
DocumentFormat.OpenXml.dll
DocumentFormat.OpenXml.Framework.dll
System.Windows.Forms.dll
System.Xml.Linq.dll
WindowsBase.dll
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text;

using System.Text.RegularExpressions;
using System.Windows.Forms;
using TFlex.DOCs.Model.Macros;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TFlex.DOCs.Model.Macros.ObjectModel;
using TFlex.DOCs.Model.References;
using TFlex.DOCs.Model.References.Files;
using TFlex.DOCs.Model.References.Users;

namespace TFlex.DOCs.MacroFiles
{
    public class Macro : MacroProvider
    {
        public Macro(MacroContext context) : base(context)
        {
        }

        public override void Run()
        {
            string localPath = null;
            string groupName = null;

            if (MessageBox.Show("Выбрать файл на компьютере?", "Выбор источника", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                using (var openFileDialog = new TFlex.DOCs.Model.Macros.ObjectModel.OpenFileDialog(Context))
                {
                    openFileDialog.Filter = "Файлы данных (*.xlsx;*.csv;*.xml)|*.xlsx;*.csv;*.xml|Все файлы (*.*)|*.*";
                    if (openFileDialog.Show())
                    {
                        localPath = openFileDialog.FileName;
                        groupName = Path.GetFileNameWithoutExtension(localPath);
                    }
                    else
                    {
                         return;
                    }
                }
            }
            else
            {
                var диалогВыбораОбъекта = СоздатьДиалогВыбораОбъектов("Файлы");
                диалогВыбораОбъекта.Заголовок = "Выбор таблицы со списком студентов";
                диалогВыбораОбъекта.МножественныйВыбор = false;
                диалогВыбораОбъекта.Фильтр = "[Тип] = 'Электронная таблица Microsoft Excel'";

                if (!диалогВыбораОбъекта.Показать())
                    return;

                var selectedObjects = диалогВыбораОбъекта.ВыбранныеОбъекты;
                if (selectedObjects == null)
                    return;

                Объект selectedObj = null;
                foreach (Объект obj in selectedObjects)
                {
                    selectedObj = obj;
                    break;
                }

                if (selectedObj == null)
                    return;

                ReferenceObject refObj = (ReferenceObject)selectedObj;
                FileObject file = refObj as FileObject;
                if (file == null)
                {
                    Сообщение("Ошибка", "Выбранный объект не является файлом.");
                    return;
                }

                file.GetHeadRevision();
                localPath = file.LocalPath;
                groupName = Path.GetFileNameWithoutExtension(file.Name.Value);
            }

            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {

                Сообщение("Ошибка", "Не удалось получить локальный путь к файлу.");
                return;
            }
            
            var excelReader = new StudentFileReader(localPath);
            var students = excelReader.ReadStudents();

            var userCreator = new UserCreator(Context);
            var targetGroup = userCreator.CreateUserGroup(groupName);

            userCreator.CreateUsers(students, targetGroup);

            var excelWriter = new StudentFileWriter(Context, localPath);
            excelWriter.WritePasswords(students);

        }
    }

    public class StudentFileReader
    {
        private readonly string _filePath;

        public StudentFileReader(string filePath)
        {
            _filePath = filePath;
        }

        public List<Student> ReadStudents()
        {
            string ext = Path.GetExtension(_filePath).ToLower();
            if (ext == ".xml") return ReadXml();
            if (ext == ".csv") return ReadCsv();

            return ReadExcel();
        }

        private List<Student> ReadXml()
        {
            var students = new List<Student>();
            try
            {
                var doc = XDocument.Load(_filePath);
                int index = 0;
                foreach (var el in doc.Descendants("record"))
                {
                    index++;
                    students.Add(new Student
                    {
                        LastName = el.Element("Фамилия")?.Value,
                        FirstName = el.Element("Имя")?.Value,
                        MiddleName = el.Element("Отчество")?.Value,
                        Login = el.Element("Логин")?.Value,
                        Row = index
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            return students;
        }

        private List<Student> ReadCsv()
        {
            var students = new List<Student>();
            try
            {
                var lines = File.ReadAllLines(_filePath, Encoding.Default);
                if (lines.Length == 0) return students;

                char delimiter = Utils.DetectDelimiter(lines[0]);

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var parts = lines[i].Split(delimiter);
                    if (parts.Length < 5) continue; 

                    students.Add(new Student
                    {
                        LastName = parts[1],
                        FirstName = parts[2],
                        MiddleName = parts[3],
                        Login = parts[4],
                        Row = i + 1
                    });
                }
            }
            catch { }
            return students;
        }

        private List<Student> ReadExcel()
        {
            var students = new List<Student>();

            using var fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using SpreadsheetDocument doc = SpreadsheetDocument.Open(fileStream, false);

            WorkbookPart workbookPart = doc.WorkbookPart;
            SharedStringTable sharedStringTable =
                workbookPart.GetPartsOfType<SharedStringTablePart>().First().SharedStringTable;

            Worksheet sheet = workbookPart.WorksheetParts.First().Worksheet;
            var rows = sheet.Descendants<Row>();

            foreach (Row row in rows.Skip(1)) // Skip header row
            {
                var cells = row.Elements<Cell>().ToList();
                if (cells.Count < 5) continue;

                var student = new Student
                {
                    LastName = GetCellValue(cells[1], sharedStringTable),
                    FirstName = GetCellValue(cells[2], sharedStringTable),
                    MiddleName = GetCellValue(cells[3], sharedStringTable),
                    Login = GetCellValue(cells[4], sharedStringTable),
                    Row = Convert.ToInt32(row.RowIndex.Value)
                };

                students.Add(student);
            }

            return students;
        }

        private string GetCellValue(Cell cell, SharedStringTable sharedStringTable)
        {
            if (cell.CellValue is null)
            {
                return string.Empty;
            }

            string value = cell.CellValue.InnerText;
            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                return sharedStringTable.ChildElements[Int32.Parse(value)].InnerText;
            }

            return value;
        }
    }

    public class StudentFileWriter : MacroProvider
    {
        private readonly string _filePath;

        public StudentFileWriter(MacroContext context, string filePath) : base(context)
        {
            _filePath = filePath;
        }

        public bool WritePasswords(List<Student> students)
        {
            string ext = Path.GetExtension(_filePath).ToLower();
            if (ext == ".xml") return WriteXml(students);
            if (ext == ".csv") return WriteCsv(students);
            
            return WriteExcel(students);
        }

        private bool WriteXml(List<Student> students)
        {
            try
            {
                if (!File.Exists(_filePath)) return false;

                var attr = File.GetAttributes(_filePath);
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(_filePath, attr & ~FileAttributes.ReadOnly);

                var doc = XDocument.Load(_filePath);
                foreach (var s in students)
                {
                    var studentEl = doc.Descendants("record")
                        .FirstOrDefault(e => e.Element("Логин")?.Value == s.Login);

                    if (studentEl != null)
                    {
                        var passEl = studentEl.Element("Пароль");
                        if (passEl == null)
                            studentEl.Add(new XElement("Пароль", s.Pass));
                        else
                            passEl.Value = s.Pass;
                    }
                }
                doc.Save(_filePath);
                return true;
            }
            catch (Exception ex)
            {
                Error("Ошибка записи в XML", ex.Message);
                return false;
            }
        }

        private bool WriteCsv(List<Student> students)
        {
            try
            {
                if (!File.Exists(_filePath)) return false;

                var attr = File.GetAttributes(_filePath);
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(_filePath, attr & ~FileAttributes.ReadOnly);

                var lines = File.ReadAllLines(_filePath, Encoding.Default);
                if (lines.Length > 0)
                {
                    char delimiter = Utils.DetectDelimiter(lines[0]);

                    var header = lines[0];
                    var parts = header.Split(delimiter);
                    int passIndex = -1;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].Trim().Equals("Пароль", StringComparison.OrdinalIgnoreCase))
                        {
                            passIndex = i;
                            break;
                        }
                    }

                    if (passIndex == -1)
                    {
                        passIndex = parts.Length;
                        lines[0] = header + delimiter + "Пароль";
                    }

                    foreach (var s in students)
                    {
                        int lineIdx = s.Row - 1;
                        if (lineIdx > 0 && lineIdx < lines.Length)
                        {
                            var line = lines[lineIdx];
                            var currentParts = line.Split(delimiter).ToList();
                            
                            while (currentParts.Count <= passIndex) currentParts.Add("");
                            
                            currentParts[passIndex] = s.Pass;
                            lines[lineIdx] = string.Join(delimiter.ToString(), currentParts);
                        }
                    }
                    File.WriteAllLines(_filePath, lines, Encoding.Default);
                }

                return true;
            }
            catch (Exception ex)
            {
                Error("Ошибка записи в CSV", ex.Message);
                return false;
            }
        }

        private bool WriteExcel(List<Student> students)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
                {
                    Error("Ошибка записи в Excel", "Файл для записи не найден: " + _filePath);
                    return false;
                }

                var attributes = File.GetAttributes(_filePath);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(_filePath, attributes & ~FileAttributes.ReadOnly);
                }

                using (var fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                using (SpreadsheetDocument doc = SpreadsheetDocument.Open(fileStream, true))
                {
                    WorkbookPart workbookPart = doc.WorkbookPart;
                    if (workbookPart == null) return false;

                    WorksheetPart worksheetPart = workbookPart.WorksheetParts.FirstOrDefault();
                    if (worksheetPart == null) return false;

                    Worksheet sheet = worksheetPart.Worksheet;
                    SheetData sheetData = sheet.GetFirstChild<SheetData>();
                    var rows = sheetData.Elements<Row>().ToList();

                    if (rows.Count == 0) return false;

                    SharedStringTablePart sstPart = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                    SharedStringTable sharedStringTable = sstPart?.SharedStringTable;

                    int passwordColIndex = FindOrCreatePasswordColumn(rows.First(), sharedStringTable);

                    foreach (var student in students)
                    {
                        Row row = rows.FirstOrDefault(r => r.RowIndex != null && r.RowIndex.Value == (uint)student.Row);
                        if (row == null)
                        {
                            row = new Row() { RowIndex = (uint)student.Row };
                            sheetData.Append(row);
                        }

                        InsertCellInWorksheet(row, passwordColIndex, student.Pass);
                    }

                    worksheetPart.Worksheet.Save();
                }

                return true;
            }
            catch (Exception ex)
            {
                Error("Ошибка записи в Excel", ex.Message);
                return false;
            }
        }

        private int FindOrCreatePasswordColumn(Row headerRow, SharedStringTable sharedStringTable)
        {
            int passwordColIndex = -1;
            
            foreach (Cell cell in headerRow.Elements<Cell>())
            {
                string header = GetCellValue(cell, sharedStringTable);
                if (!string.IsNullOrEmpty(header) && Utils.CleanString(header).ToLower() == "пароль")
                {
                    passwordColIndex = GetColumnIndex(cell.CellReference.Value);
                    break;
                }
            }

            if (passwordColIndex == -1)
            {
                int lastCol = headerRow.Elements<Cell>().Max(c => GetColumnIndex(c.CellReference.Value));
                passwordColIndex = lastCol + 1;
                InsertCellInWorksheet(headerRow, passwordColIndex, "Пароль");
            }

            return passwordColIndex;
        }
        
        private string GetCellValue(Cell cell, SharedStringTable sharedStringTable)
        {
            if (cell.CellValue is null)
            {
                return string.Empty;
            }

            string value = cell.CellValue.InnerText;
            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                return sharedStringTable.ChildElements[Int32.Parse(value)].InnerText;
            }

            return value;
        }

        private void InsertCellInWorksheet(Row row, int columnIndex, string value)
        {
            string cellReference = GetColumnName(columnIndex) + row.RowIndex?.ToString();

            Cell refCell = null;
            foreach (Cell cell in row.Elements<Cell>())
            {
                if (string.Compare(cell.CellReference.Value, cellReference, true) > 0)
                {
                    refCell = cell;
                    break;
                }
            }

            Cell newCell = new Cell() { CellReference = cellReference };
            if (refCell != null)
            {
                row.InsertBefore(newCell, refCell);
            }
            else
            {
                row.Append(newCell);
            }

            newCell.CellValue = new CellValue(value);
            newCell.DataType = new DocumentFormat.OpenXml.EnumValue<CellValues>(CellValues.String);
        }

        private string GetColumnName(int columnIndex)
        {
            int dividend = columnIndex;
            string columnName = String.Empty;
            int modulo;

            while (dividend > 0)
            {
                modulo = (dividend - 1) % 26;
                columnName = Convert.ToChar(65 + modulo).ToString() + columnName;
                dividend = (int)((dividend - modulo) / 26);
            }

            return columnName;
        }

        private int GetColumnIndex(string cellReference)
        {
            if (string.IsNullOrEmpty(cellReference))
                return 0;

            string columnLetter = new string(cellReference.Where(char.IsLetter).ToArray());
            int columnIndex = 0;
            int factor = 1;
            for (int i = columnLetter.Length - 1; i >= 0; i--)
            {
                columnIndex += (columnLetter[i] - 'A' + 1) * factor;
                factor *= 26;
            }
            return columnIndex;
        }
    }

    public class UserCreator : MacroProvider
    {
        private readonly UserReference _userReference;

        public UserCreator(MacroContext context) : base(context)
        {
            _userReference = new UserReference(context.Connection);
        }

        public ReferenceObject CreateUserGroup(string groupName)
        {
            var groupStudents = FindGroupByName("Студенты");
            if (groupStudents is null)
            {
                Error("Внимание!", "Не найдена группа 'Студенты'");
                return null;
            }

            var targetGroupClass = groupStudents.Class;
            var groupNameParam = _userReference.ParameterGroup.Parameters.FindByName("Наименование");

            _userReference.LoadSettings.AddGroup(_userReference.ParameterGroup);

            var targetGroup = _userReference.CreateReferenceObject(groupStudents, targetGroupClass) as ReferenceObject;
            targetGroup[groupNameParam].Value = groupName;
            targetGroup.EndChanges();

            return targetGroup;
        }

        public void CreateUsers(List<Student> students, ReferenceObject targetGroup)
        {
            var employeeClass = _userReference.Classes.Find("Сотрудник");
            var parameterGroup = employeeClass.ParameterGroups.Find("Параметры пользователя");

            foreach (var student in students)
            {
                student.Pass = Utils.GeneratePin();
                
                var user = _userReference
                    .GetAllUsers().FirstOrDefault(user => user.Login.Value == student.Login);
                if (user != null)
                {
                    user.BeginChanges();
                    user[parameterGroup.Parameters.FindByName("Фамилия")].Value = student.LastName;
                    user[parameterGroup.Parameters.FindByName("Имя")].Value = student.FirstName;
                    user[parameterGroup.Parameters.FindByName("Отчество")].Value = student.MiddleName;
                    user[parameterGroup.Parameters.FindByName("Логин")].Value = student.Login;
                    user[parameterGroup.Parameters.FindByName("Короткое имя")].Value = Utils.BuildShortName(student);
                    user[parameterGroup.Parameters.FindByName("Пароль")].Value = student.Pass;
                    user.EndChanges();
                    
                    var links1 = Reference.CreateComplexHierarchyLinks(new[] { targetGroup }, new[] { user });
                    Reference.EndChanges(links1);
                    continue;
                }
                
                user = _userReference.CreateReferenceObject(employeeClass) as User;
                user[parameterGroup.Parameters.FindByName("Фамилия")].Value = student.LastName;
                user[parameterGroup.Parameters.FindByName("Имя")].Value = student.FirstName;
                user[parameterGroup.Parameters.FindByName("Отчество")].Value = student.MiddleName;
                user[parameterGroup.Parameters.FindByName("Логин")].Value = student.Login;
                user[parameterGroup.Parameters.FindByName("Короткое имя")].Value = Utils.BuildShortName(student);
                user[parameterGroup.Parameters.FindByName("Пароль")].Value = student.Pass;

                user.EndChanges();

                var links = Reference.CreateComplexHierarchyLinks(new[] { targetGroup }, new[] { user });
                Reference.EndChanges(links);
            }
        }

        private UserReferenceObject FindGroupByName(string groupName)
        {
            var groups = _userReference.Objects.AsList;
            foreach (var group in groups)
            {
                if (group.FullName.ToString() == groupName)
                {
                    return group;
                }
            }
            return null;
        }
    }

    public static class Utils
    {
        private static readonly Random _random = new Random();

        public static string BuildShortName(Student student)
        {
            if (student == null)
                return string.Empty;

            string firstInitial = string.IsNullOrWhiteSpace(student.FirstName)
                ? string.Empty
                : student.FirstName.Trim().Substring(0, 1) + ".";

            string middleInitial = string.IsNullOrWhiteSpace(student.MiddleName)
                ? string.Empty
                : student.MiddleName.Trim().Substring(0, 1) + ".";

            string surname = student.LastName ?? string.Empty;
            string shortName = string.Format("{0} {1}{2}", surname.Trim(), firstInitial, middleInitial).Trim();

            return string.IsNullOrWhiteSpace(shortName) ? student.Login ?? string.Empty : shortName;
        }

        public static string GeneratePin()
        {
            int pin = _random.Next(10000, 100000); // from 10000 to 99999
            return pin.ToString();
        }
        
        public static string CleanString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            string cleaned = Regex.Replace(input, @"[^\w\s\.\-]", "", RegexOptions.None);
            
            cleaned = Regex.Replace(cleaned, @"\s+", " ");

            return cleaned.Trim();
        }

        public static char DetectDelimiter(string line)
        {
            if (string.IsNullOrEmpty(line)) return ';';
            
            char[] possibleDelimiters = new[] { ';', ',', '\t' };
            int maxCount = -1;
            char bestDelimiter = ';';

            foreach (var d in possibleDelimiters)
            {
                int count = line.ToCharArray().Count(c => c == d);
                 if (count > maxCount)
                {
                    maxCount = count;
                    bestDelimiter = d;
                }
            }
            
            return maxCount > 0 ? bestDelimiter : ';';
        }
    }

    public class Student
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public string Login { get; set; }
        public string Pass { get; set; }
        public int Row { get; set; }
    }
}

