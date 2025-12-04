# SMM Planner

Система планирования публикаций в социальных сетях (ВКонтакте и Telegram).

## Требования

- .NET 8.0 SDK
- PostgreSQL (локальная установка)
- Visual Studio 2022 или VS Code

## Установка и запуск

### 1. Создать базу данных PostgreSQL

```sql
CREATE DATABASE smm_planner;
```

### 2. Настроить appsettings.json

Обновите строку подключения к базе данных:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=smm_planner;Username=postgres;Password=your_password"
  }
}
```

### 3. Применить миграции

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

Если у вас не установлен EF Core Tools:

```bash
dotnet tool install --global dotnet-ef
```

### 4. Установить клиентские библиотеки (Bootstrap, jQuery)

```bash
# В папке wwwroot/lib
# Скачайте Bootstrap и jQuery или используйте CDN в _Layout.cshtml
```

Или используйте CDN (уже включено в _Layout.cshtml через NuGet пакеты).

### 5. Запустить приложение

```bash
dotnet run
```

Приложение будет доступно по адресу:
- Web: `https://localhost:5001` или `http://localhost:5000`
- Swagger API: `https://localhost:5001/swagger`

## Функциональность

### Авторизация и регистрация

- Регистрация новых пользователей
- Вход в систему
- Выход из аккаунта

### Проекты

- Создание проектов
- Управление проектами
- Связь проектов с каналами

### Каналы

- Добавление каналов ВКонтакте (с токеном в AuthRef)
- Добавление каналов Telegram (с bot token в AuthRef)
- Управление каналами

### Публикации

- Создание публикаций с текстом и файлами
- Планирование публикаций
- Немедленная публикация
- Поддержка DeltaQuill формата для форматированного текста

### Файлы

- Загрузка файлов (изображения, видео, документы)
- Хранение файлов на локальном диске
- Связь файлов с публикациями

### Фоновый сервис

- Автоматическая публикация по расписанию
- Повторные попытки при ошибках
- Обработка до 10 параллельных публикаций

## API Endpoints

### Publications
- `GET /api/publications` - Получить все публикации
- `GET /api/publications/{id}` - Получить публикацию
- `POST /api/publications` - Создать публикацию
- `PUT /api/publications/{id}` - Обновить публикацию
- `DELETE /api/publications/{id}` - Удалить публикацию
- `POST /api/publications/{id}/publish-now` - Опубликовать немедленно
- `GET /api/publications/calendar` - Получить публикации для календаря
- `GET /api/publications/project/{projectId}` - Получить публикации проекта

### Channels
- `GET /api/channels` - Получить все каналы
- `GET /api/channels/{id}` - Получить канал
- `POST /api/channels` - Создать канал
- `PUT /api/channels/{id}` - Обновить канал
- `DELETE /api/channels/{id}` - Удалить канал

### Projects
- `GET /api/projects` - Получить все проекты
- `GET /api/projects/{id}` - Получить проект
- `POST /api/projects` - Создать проект
- `PUT /api/projects/{id}` - Обновить проект
- `DELETE /api/projects/{id}` - Удалить проект

### Files
- `POST /api/files/upload` - Загрузить файл
- `GET /api/files/{id}` - Получить информацию о файле
- `GET /api/files/{id}/download` - Скачать файл
- `DELETE /api/files/{id}` - Удалить файл

## Настройка каналов

### ВКонтакте

```json
{
  "projectId": "guid",
  "displayName": "Моя группа ВК",
  "type": 1,
  "externalId": "-123456789",
  "authRef": "{\"token\": \"your_vk_token\"}"
}
```

### Telegram

```json
{
  "projectId": "guid",
  "displayName": "Мой Telegram канал",
  "type": 2,
  "externalId": "@my_channel",
  "authRef": "123456789:ABCdefGHIjklMNOpqrsTUVwxyz"
}
```

## Структура проекта

```
SmmGab/
├── Controllers/
│   ├── Api/          # API контроллеры
│   ├── AccountController.cs
│   └── HomeController.cs
├── Domain/
│   ├── Enums/        # Перечисления
│   └── Models/       # Модели данных
├── Data/
│   └── ApplicationDbContext.cs
├── Infrastructure/
│   ├── Connectors/   # Публикаторы (VK, Telegram)
│   └── Services/     # Сервисы (FileStorage, DeltaFileExtractor)
├── Background/       # Фоновый сервис планирования
├── Application/
│   └── Abstractions/ # Интерфейсы
└── Views/            # MVC Views

