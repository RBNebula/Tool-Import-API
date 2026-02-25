namespace MineMogul.ToolImportApi.Api
{
    public sealed class ToolRegistrationResult
    {
        public bool Success;
        public string Message;
        public string FullToolId;

        public static ToolRegistrationResult Ok(string fullToolId, string message)
        {
            return new ToolRegistrationResult
            {
                Success = true,
                Message = message,
                FullToolId = fullToolId
            };
        }

        public static ToolRegistrationResult Fail(string fullToolId, string message)
        {
            return new ToolRegistrationResult
            {
                Success = false,
                Message = message,
                FullToolId = fullToolId
            };
        }
    }
}
