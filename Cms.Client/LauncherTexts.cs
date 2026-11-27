using System.Globalization;
using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// Every text of the lock screen (Cms.Launcher) in Ukrainian and English. Each text is a required
/// member, so a text that one language misses is a compile error. {0}, {1} ... are filled by Format.
/// The language comes from the club setting (client-settings.json, see Cms.Shared.ClientSettings);
/// without it the lock screen is Ukrainian.
/// </summary>
public sealed class LauncherTexts
{
    public required string Language { get; init; }

    // Window and lock overlay
    public required string WindowTitle { get; init; }
    public required string LockedTitle { get; init; }
    public required string LockedHint { get; init; }
    public required string LockedTimeLeft { get; init; }
    public required string AgentNotResponding { get; init; }
    public required string NoAgentState { get; init; }
    public required string StaffHint { get; init; }
    public required string LanguageSwitch { get; init; }

    // Login panel
    public required string LoginRequired { get; init; }
    public required string UsernameLabel { get; init; }
    public required string SecretLabel { get; init; }
    public required string LoginButton { get; init; }
    public required string LoginBusy { get; init; }
    public required string EnterUsername { get; init; }
    public required string WrongLogin { get; init; }
    public required string ServerError { get; init; }
    public required string NoConnection { get; init; }
    public required string EnterSecret { get; init; }
    public required string LoginLocked { get; init; }
    public required string LoginStarting { get; init; }
    public required string InsufficientBalance { get; init; }
    public required string PlayerHasDebt { get; init; }
    public required string DeviceBusy { get; init; }
    public required string PackageUnavailable { get; init; }
    public required string NoTariff { get; init; }
    public required string AgentUnavailable { get; init; }
    public required string SessionGone { get; init; }

    // How the player pays (offers of the PC's zone)
    public required string OffersTitle { get; init; }
    public required string OfferBalance { get; init; }
    public required string OfferBalanceHint { get; init; }
    public required string OfferMinutes { get; init; }
    public required string OfferPackage { get; init; }
    public required string OffersUnavailable { get; init; }

    // Session panel
    public required string SessionActive { get; init; }
    public required string Guest { get; init; }
    public required string TimeLeftLabel { get; init; }
    public required string OpenEndedLabel { get; init; }
    public required string ExtendButton { get; init; }
    public required string ExtendTitle { get; init; }
    public required string ExtendHint { get; init; }
    public required string LowTimeBanner { get; init; }
    public required string LastMinuteBanner { get; init; }
    public required string BalanceTimeLabel { get; init; }
    public required string SessionPaused { get; init; }
    public required string TariffLine { get; init; }
    public required string PackageLine { get; init; }
    public required string BonusLine { get; init; }
    public required string EndSessionButton { get; init; }
    public required string EndSessionTitle { get; init; }
    public required string EndSessionPrepaid { get; init; }
    public required string EndSessionBalance { get; init; }
    public required string EndSessionFailed { get; init; }

    // Game grid (the catalog of the PC's zone, only during a session)
    public required string GamesTitle { get; init; }
    public required string GamesSearch { get; init; }
    public required string GamesAll { get; init; }
    public required string GamesEmpty { get; init; }
    public required string GamesNoMatch { get; init; }
    public required string GameStartTitle { get; init; }
    public required string GameStarting { get; init; }
    public required string GameStartFailed { get; init; }
    public required string GameNow { get; init; }

    // Staff PIN unlock (Ctrl+Shift+U)
    public required string UnlockTitle { get; init; }
    public required string PinWait { get; init; }
    public required string PinNotSet { get; init; }
    public required string PinPrompt { get; init; }
    public required string PinDialogTitle { get; init; }
    public required string PinDialogHeader { get; init; }
    public required string PinUnlocked { get; init; }
    public required string PinWrong { get; init; }
    public required string Done { get; init; }
    public required string Error { get; init; }
    public required string Ok { get; init; }
    public required string Cancel { get; init; }

    public static readonly LauncherTexts Uk = new()
    {
        Language = ClientLanguages.Ukrainian,
        WindowTitle = "PC Club — екран блокування",
        LockedTitle = "[ ЗАБЛОКОВАНО ]",
        LockedHint = "> УВІЙДІТЬ АБО ЗВЕРНІТЬСЯ ДО ПЕРСОНАЛУ",
        LockedTimeLeft = "Залишилось: {0}",
        AgentNotResponding = "> АГЕНТ НЕ ВІДПОВІДАЄ — ЗВЕРНІТЬСЯ ДО ПЕРСОНАЛУ",
        NoAgentState = "> НЕМАЄ СТАНУ АГЕНТА — ЗВЕРНІТЬСЯ ДО ПЕРСОНАЛУ",
        StaffHint = "CTRL+SHIFT+U :: РОЗБЛОКУВАННЯ ПЕРСОНАЛОМ",
        LanguageSwitch = "EN",

        LoginRequired = "// ПОТРІБЕН ВХІД",
        UsernameLabel = "> ЛОГІН",
        SecretLabel = "> PIN АБО ПАРОЛЬ",
        LoginButton = "[ УВІЙТИ ]",
        LoginBusy = "Вхід...",
        EnterUsername = "Введіть логін",
        WrongLogin = "Невірний логін або пароль",
        ServerError = "Помилка сервера ({0})",
        NoConnection = "Немає зв'язку з сервером: {0}",
        EnterSecret = "Введіть PIN або пароль",
        LoginLocked = "Забагато невдалих спроб. Спробуйте через {0} хв або зверніться до адміністратора.",
        LoginStarting = "Вхід виконано. Сесія починається…",
        InsufficientBalance = "Недостатньо коштів на балансі. Поповніть баланс на рецепції.",
        PlayerHasDebt = "На вашому рахунку борг. Сплатіть його на рецепції або виберіть передоплачений час.",
        DeviceBusy = "На цьому ПК вже йде сесія.",
        PackageUnavailable = "Цей варіант зараз недоступний. Виберіть інший.",
        NoTariff = "Для цього ПК не задано тариф. Зверніться до адміністратора.",
        AgentUnavailable = "Агент ПК не відповідає. Вхід зараз неможливий, зверніться до адміністратора.",
        SessionGone = "Сесію вже завершено.",

        OffersTitle = "> ЯК ОПЛАТИТИ",
        OfferBalance = "З балансу, поки вистачить коштів",
        OfferBalanceHint = "Тариф «{0}»: {1}/год зараз",
        OfferMinutes = "{0} год — {1}",
        OfferPackage = "Пакет «{0}» — {1}, до {2}",
        OffersUnavailable = "Ціни зараз недоступні (немає зв'язку з сервером).",

        SessionActive = "АКТИВНО",
        Guest = "Гість",
        TimeLeftLabel = "> ЗАЛИШИЛОСЬ ЧАСУ",
        OpenEndedLabel = "> СЕСІЯ БЕЗ ЛІМІТУ ЧАСУ",
        ExtendButton = "⏱ ПОДОВЖИТИ",
        ExtendTitle = "Подовження сесії",
        ExtendHint = "Щоб подовжити сесію, зверніться до адміністратора або поповніть баланс.",
        LowTimeBanner = "⚠ Залишилось {0} хв! Поповніть баланс або подовжте сесію",
        LastMinuteBanner = "⚠ Залишилась 1 хвилина! Збережіть гру: ПК зараз заблокується.",
        BalanceTimeLabel = "> ЧАС З БАЛАНСУ (ЗА ПОТОЧНОЮ ЦІНОЮ)",
        SessionPaused = "> СЕСІЮ ПРИЗУПИНЕНО — ЗВЕРНІТЬСЯ ДО АДМІНІСТРАТОРА",
        TariffLine = "Тариф: {0}",
        PackageLine = "Пакет: {0}",
        BonusLine = "бонус {0}",
        EndSessionButton = "⏹ ЗАВЕРШИТИ СЕСІЮ",
        EndSessionTitle = "Завершення сесії",
        EndSessionPrepaid = "Завершити сесію зараз? Невикористаний оплачений час не повертається.",
        EndSessionBalance = "Завершити сесію зараз? Зіграний час буде списано з балансу.",
        EndSessionFailed = "Не вдалося завершити сесію: {0}",

        GamesTitle = "// ІГРИ",
        GamesSearch = "Пошук гри",
        GamesAll = "Усі",
        GamesEmpty = "Каталог ігор порожній. Зверніться до адміністратора.",
        GamesNoMatch = "Нічого не знайдено",
        GameStartTitle = "Запуск гри",
        GameStarting = "Запуск: {0}",
        GameStartFailed = "Не вдалося запустити «{0}»: {1}",
        GameNow = "Зараз: {0}",

        UnlockTitle = "Розблокування",
        PinWait = "Забагато невірних спроб. Зачекайте {0} с.",
        PinNotSet = "PIN персоналу не задано. Власник задає його в адмін-панелі (Налаштування).",
        PinPrompt = "Введіть PIN персоналу. Розблокування діє до наступного блокування.",
        PinDialogTitle = "Розблокування персоналом",
        PinDialogHeader = "PIN ПЕРСОНАЛУ",
        PinUnlocked = "Розблоковано персоналом.\n\nРозблокування діє до наступного блокування від сервера.",
        PinWrong = "Невірний PIN.",
        Done = "Готово",
        Error = "Помилка",
        Ok = "[ OK ]",
        Cancel = "[ СКАСУВАТИ ]",
    };

    public static readonly LauncherTexts En = new()
    {
        Language = ClientLanguages.English,
        WindowTitle = "PC Club — lock screen",
        LockedTitle = "[ LOCKED ]",
        LockedHint = "> LOG IN OR ASK THE STAFF",
        LockedTimeLeft = "Time left: {0}",
        AgentNotResponding = "> THE AGENT DOES NOT ANSWER — ASK THE STAFF",
        NoAgentState = "> NO AGENT STATE — ASK THE STAFF",
        StaffHint = "CTRL+SHIFT+U :: STAFF UNLOCK",
        LanguageSwitch = "UA",

        LoginRequired = "// LOGIN REQUIRED",
        UsernameLabel = "> USERNAME",
        SecretLabel = "> PIN OR PASSWORD",
        LoginButton = "[ LOG IN ]",
        LoginBusy = "Logging in...",
        EnterUsername = "Enter your username",
        WrongLogin = "Wrong username or password",
        ServerError = "Server error ({0})",
        NoConnection = "No connection to the server: {0}",
        EnterSecret = "Enter your PIN or password",
        LoginLocked = "Too many failed attempts. Try again in {0} min or ask the staff.",
        LoginStarting = "Logged in. The session starts…",
        InsufficientBalance = "There is not enough money on your balance. Top up at the desk.",
        PlayerHasDebt = "Your account has a debt. Pay it at the desk or choose prepaid time.",
        DeviceBusy = "This PC already has a session.",
        PackageUnavailable = "This choice is not available now. Choose another one.",
        NoTariff = "This PC has no tariff. Ask the staff.",
        AgentUnavailable = "The PC agent does not answer. Login is not possible now; ask the staff.",
        SessionGone = "The session has already ended.",

        OffersTitle = "> HOW TO PAY",
        OfferBalance = "From my balance while the money lasts",
        OfferBalanceHint = "Tariff \"{0}\": {1}/h now",
        OfferMinutes = "{0} h — {1}",
        OfferPackage = "Package \"{0}\" — {1}, until {2}",
        OffersUnavailable = "Prices are not available now (no connection to the server).",

        SessionActive = "ACTIVE",
        Guest = "Guest",
        TimeLeftLabel = "> TIME LEFT",
        OpenEndedLabel = "> SESSION WITHOUT A TIME LIMIT",
        ExtendButton = "⏱ EXTEND",
        ExtendTitle = "Extend the session",
        ExtendHint = "To extend the session, ask the staff or top up your balance.",
        LowTimeBanner = "⚠ {0} min left! Top up your balance or extend the session",
        LastMinuteBanner = "⚠ 1 minute left! Save your game: the PC locks soon.",
        BalanceTimeLabel = "> TIME FROM THE BALANCE (AT THE CURRENT PRICE)",
        SessionPaused = "> THE SESSION IS PAUSED — ASK THE STAFF",
        TariffLine = "Tariff: {0}",
        PackageLine = "Package: {0}",
        BonusLine = "bonus {0}",
        EndSessionButton = "⏹ END THE SESSION",
        EndSessionTitle = "End the session",
        EndSessionPrepaid = "End the session now? Unused paid time is not refunded.",
        EndSessionBalance = "End the session now? The time played is paid from your balance.",
        EndSessionFailed = "The session could not be ended: {0}",

        GamesTitle = "// GAMES",
        GamesSearch = "Search games",
        GamesAll = "All",
        GamesEmpty = "The game catalog is empty. Ask the staff.",
        GamesNoMatch = "Nothing found",
        GameStartTitle = "Start a game",
        GameStarting = "Starting: {0}",
        GameStartFailed = "Could not start {0}: {1}",
        GameNow = "Now: {0}",

        UnlockTitle = "Unlock",
        PinWait = "Too many wrong attempts. Wait {0} s.",
        PinNotSet = "The staff PIN is not set. The owner sets it in the admin UI (Settings).",
        PinPrompt = "Enter the staff PIN. The unlock lasts until the next lock.",
        PinDialogTitle = "Staff unlock",
        PinDialogHeader = "STAFF PIN",
        PinUnlocked = "Unlocked by the staff.\n\nThe unlock lasts until the server locks the PC again.",
        PinWrong = "Wrong PIN.",
        Done = "Done",
        Error = "Error",
        Ok = "[ OK ]",
        Cancel = "[ CANCEL ]",
    };

    /// <summary>The texts of a language code (uk or en); Ukrainian for anything else.</summary>
    public static LauncherTexts For(string? language) =>
        ClientLanguages.Normalize(language) == ClientLanguages.English ? En : Uk;

    /// <summary>The other language (for the UA / EN switch on the lock screen).</summary>
    public LauncherTexts Other => Language == ClientLanguages.English ? Uk : En;

    public static string Format(string text, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, text, args);

    /// <summary>All texts with their names (for the tests: none empty, the same placeholders in both languages).</summary>
    public IReadOnlyDictionary<string, string> All() =>
        typeof(LauncherTexts).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => (string)p.GetValue(this)!);
}

/// <summary>Money and time on the lock screen, the same way as the admin UI shows them.</summary>
public static class LauncherFormat
{
    /// <summary>Two decimals and the club currency symbol: "150.00 ₴", "12.50 €".</summary>
    public static string Money(decimal amount, ClientSettings settings) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + settings.CurrencySymbol;

    /// <summary>Remaining time as hh:mm:ss (hours above 99 stay as they are).</summary>
    public static string Clock(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
