namespace VanyaTools.Updater
{
    // A rejected request can be started again. A timeout/server error after
    // submission can have created a paid prediction without returning its ID.
    internal static class ReplicateSubmissionFailure
    {
        internal static bool DefinitelyRejected(int status)=>status>=400 && status<500 && status!=408;
        internal static string DescribeUnknown(int status,string details)=>
            "Replicate вернул HTTP "+status+" при запуске модели. Превью загружено, но номер генерации не получен. " +
            "Сервер мог создать платный запрос; автоматический повтор заблокирован. " +
            "Проверьте историю запросов Replicate перед новой генерацией.\n"+details;
    }
}
