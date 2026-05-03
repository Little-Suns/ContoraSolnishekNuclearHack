# T-FLEX DOCs Educational Workflow Automation

Automation macros for T-FLEX DOCs that help instructors manage student onboarding and assignment distribution at scale.

## Project Overview

This repository contains two production-oriented C# macros:

1. **Student Account Provisioning** (`Создание_Пользователей.cs`)  
   Imports student records from Excel/CSV/XML, creates or updates users, and writes generated passwords back to the source file.
2. **Assignment Distribution** (`Распределение_Заданий.cs`)  
   Distributes assignment variants across student folders with a collision-minimizing strategy and optional CAD export for `.grb` files.

The solution is designed for real classroom operations where hundreds of accounts and files must be handled consistently and quickly.

## Key Features

### 1. Student Account Provisioning

- Supports `.xlsx`, `.csv`, and `.xml` input files
- Automatically creates (or reuses) a target student group
- Creates new users and updates existing users by login
- Generates random 5-digit PIN passwords
- Builds short names in `Surname I.O.` format
- Writes passwords back to the original input file

### 2. Assignment Distribution

- Reads assignment sets from folder structure under `Задания`
- Supports assignment file types: `.grb`, `.pdf`, `.tif`, `.tiff`
- Restricts access to source assignment folders (teachers-only)
- Uses multi-attempt randomized planning to minimize duplicate assignment combinations
- Copies one variant from each work set to every student
- Supports optional CAD export: `.grb -> .pdf` or `.tiff`
- Produces a per-group distribution report

## Repository Structure

| File | Purpose |
|---|---|
| `Создание_Пользователей.cs` | Student import, group creation, user provisioning, password write-back |
| `Распределение_Заданий.cs` | Assignment planning, security setup, file copy/export, reporting |
| `Контора_Солнышек(1ver).pdf` / `.pptx` | Project materials (presentation and report) |

## Input Data Format

Required columns/fields:

- `Фамилия` (Last Name)
- `Имя` (First Name)
- `Отчество` (Middle Name)
- `Логин` (Login)

Example CSV:

```csv
№;Фамилия;Имя;Отчество;Логин
1;Иванов;Петр;Сергеевич;ivanov.ps
2;Петрова;Анна;Ивановна;petrova.ai
```

## Expected Folder Layout

Before distribution:

```text
Файлы/
├── Задания/
│   ├── Работа 1/
│   ├── Работа 2/
│   └── ...
└── Студенты/
    └── <GroupName>/
        ├── <Student 1>/
        ├── <Student 2>/
        └── ...
```

After distribution (inside each student folder):

```text
Задания/
├── Работа 1.pdf
├── Работа 2.tiff
└── ...
```

## Setup Requirements

- T-FLEX DOCs with API-enabled macro runtime
- .NET Framework 4.5+ compatible environment
- Optional: T-FLEX CAD (required for `.grb` export mode)

Required entities in T-FLEX DOCs:

- User group: `Студенты`
- User group: `Преподаватели`
- Access group: `Редакторский`
- File reference root folders: `Задания`, `Студенты`

## Usage

### Step 1: Create Student Accounts

1. Run macro from `Создание_Пользователей.cs`.
2. Select a local file (`.xlsx/.csv/.xml`) or a file from the `Файлы` reference.
3. Wait for completion.
4. Keep the updated source file — it contains generated passwords.

### Step 2: Distribute Assignments

1. Prepare assignment variants under `Файлы/Задания/Работа N`.
2. Ensure student folders exist under `Файлы/Студенты/<GroupName>/`.
3. Run:
   - `Run()` for direct file copy
   - `RunWithCADExport()` for `.grb` conversion + copy
4. Review the generated report in macro output.

## Technical Notes

- Assignment planning performs multiple randomized attempts and selects the plan with the fewest duplicate student signatures.
- Access inheritance for the source `Задания` folder is disabled and replaced with explicit teacher access.
- Password write-back supports XML, CSV, and Excel (OpenXML).

## Limitations

- Assignment macro processes only `.grb`, `.pdf`, `.tif`, `.tiff` as assignment sources.
- CAD export requires an active CAD document provider.
- CSV parsing is delimiter-aware (`;`, `,`, tab) but assumes simple row structure.

## Troubleshooting

| Issue | Resolution |
|---|---|
| `Не найдена группа 'Студенты'` | Create the `Студенты` group in user reference |
| `Не найдена папка 'Задания'` | Create `Задания` in the root of `Файлы` |
| CAD export is unavailable | Start T-FLEX CAD or run non-export mode (`Run`) |
| Passwords were not written | Ensure the file is writable and not locked by another app |

## License

No explicit license file is currently provided in this repository.
