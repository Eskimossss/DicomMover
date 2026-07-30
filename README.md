# DicomMover

![Версия](https://img.shields.io/badge/version-1.0-blue)
![Платформа](https://img.shields.io/badge/platform-Windows-success)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

DicomMover — программа для автоматической отправки DICOM-файлов из наблюдаемой папки на PACS.

---

## Основные возможности

- Автоматическое наблюдение за папкой.
- Отправка DICOM через C-STORE.
- Проверка PACS через C-ECHO.
- Очередь отправки на базе SQLite.
- Защита от повторной отправки файлов.
- Повторные попытки после ошибок.
- История отправки.
- Журнал работы.
- Графический интерфейс Windows (WPF).

---

## Интерфейс

*Скриншот будет добавлен после выхода версии 1.1.*

---

## Требования

- Windows 10 / Windows 11
- .NET 8 Runtime

---

## Разработка

Для сборки проекта потребуется:

- .NET 8 SDK
- Visual Studio 2022

Запуск:

```powershell
dotnet restore
dotnet run
```

Сборка:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## Структура проекта

```
Models/
Services/
App.xaml
MainWindow.xaml
DicomMover.csproj
```

---

## Версия

Текущая версия:

**1.0**

---

## Лицензия

Проект находится в стадии активной разработки.