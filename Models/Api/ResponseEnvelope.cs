namespace Relisten.Api.Models.Api
{
    public enum ApiErrorCode
    {
        NotFound = 404
    }

    public class ResponseEnvelope
    {
        public bool success { get; set; }

        public ApiErrorCode? error_code { get; set; }

        public object data { get; set; }

        public static ResponseEnvelope Success(object data = null) {
            var r = new ResponseEnvelope();
            r.success = true;
            r.error_code = null;
            r.data = data;
            return r;
        }

        public static ResponseEnvelope Error(ApiErrorCode code, object data = null) {
            var r = new ResponseEnvelope();
            r.success = false;
            r.error_code = code;
            r.data = data;
            return r;
        }
    }
}