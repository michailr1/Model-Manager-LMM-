# Local Model Manager

[![Build and test](https://github.com/michailr1/Model-Manager-LMM-/actions/workflows/ci.yml/badge.svg)](https://github.com/michailr1/Model-Manager-LMM-/actions/workflows/ci.yml)
[![Windows release](https://github.com/michailr1/Model-Manager-LMM-/actions/workflows/release.yml/badge.svg)](https://github.com/michailr1/Model-Manager-LMM-/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/michailr1/Model-Manager-LMM-)](https://github.com/michailr1/Model-Manager-LMM-/releases/latest)

Windows-приложение для подключения уже скачанных GGUF-моделей к **LM Studio** и подготовки API для **Hermes**.

**GGUF → LM Studio → оценка памяти → загрузка → API → Hermes**

## Установка

1. Откройте [последний выпуск](https://github.com/michailr1/Model-Manager-LMM-/releases/latest).
2. Скачайте **LocalModelManager-win-x64-Setup.exe** из Assets и запустите.
3. Откройте Local Model Manager через ярлык.

Требуется Windows 11 x64. .NET включён в установщик. Отдельно установите [LM Studio](https://lmstudio.ai/) с доступным `lms.exe`; для оценки памяти нужен драйвер NVIDIA с `nvidia-smi`. Референсная конфигурация — RTX 4090 24 GB, фактическая память определяется во время работы.

Установщик пока не подписан сертификатом Authenticode, поэтому Windows может показать предупреждение о неизвестном издателе.

## Что умеет

- Принимать GGUF через выбор файла или перетаскивание; автоматически подставлять локальную категорию.
- Подключать файл символической ссылкой без переноса или копирования десятков гигабайт. Полная копия — только по отдельному подтверждению.
- Спрашивать у LM Studio оценку памяти для контекста **65 536** токенов и подбирать долю GPU с запасом VRAM.
- Загружать модель с проверкой VRAM, RAM и фактического контекста.
- Проверять `/v1/models` и ответ `/v1/chat/completions`.
- Готовить YAML-профили Hermes для этого ПК и LAN; определять адрес физического сетевого интерфейса.
- Получать обновления из GitHub Releases.

Интерфейс текущего выпуска содержит отдельные вкладки подключения, памяти, загрузки и API. Подробный порядок работы и ограничения: [руководство](docs/DEVELOPMENT.md).

## Автообновление

Установленная версия проверяет стабильные выпуски при запуске и каждые 6 часов. Новый пакет скачивается в фоне и проверяется Velopack. Кнопка **«Обновить и перезапустить»** становится доступной после загрузки пакета и завершения операции с моделью.

Сервер LM Studio работает отдельным процессом: обновление менеджера не выгружает модель. Настройки обновления можно раскрыть в верхней панели. Источник уже настроен на этот репозиторий; автопроверку можно отключить. Ошибки сети видны в статусе и не мешают управлению моделью.

Первый раз нужен Setup.exe. Запуск из исходников или `bin/Debug` не является установленной копией и не обновляет сам себя. Приватные репозитории и prerelease-каналы пока не поддерживаются.

[Как выпустить новую версию](docs/UPDATES.md) · [История изменений](CHANGELOG.md) · [Проверки](VERIFICATION.md) · [Внести изменения](CONTRIBUTING.md)

## Сборка из исходников

Нужен .NET 8 SDK с поддержкой Windows Desktop.

```powershell
git clone https://github.com/michailr1/Model-Manager-LMM-.git
cd Model-Manager-LMM-
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/LocalModelManager.App
```

Сборка автономного установщика:

```powershell
./scripts/Build-Release.ps1
```

Результат — `artifacts/<версия>/releases/`. Модели, профили пользователя, токены и каталоги сборки в Git не включаются.
