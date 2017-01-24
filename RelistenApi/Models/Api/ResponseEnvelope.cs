using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models.Api
{
    public enum ApiErrorCode
    {
        NoError = 0,
        NotFound = 404
    }

    public class ResponseEnvelope<T>
    {
        [Required]
        public bool success { get; set; }

        [Required]
        public ApiErrorCode error_code { get; set; }

        [Required]
        public T data { get; set; }

        public static ResponseEnvelope<T> Success(T data = default(T)) {
            var r = new ResponseEnvelope<T>();
            r.success = true;
            r.error_code = ApiErrorCode.NoError;
            r.data = data;
            return r;
        }

        public static ResponseEnvelope<T> Error(ApiErrorCode code, T data = default(T)) {
            var r = new ResponseEnvelope<T>();
            r.success = false;
            r.error_code = code;
            r.data = data;
            return r;
        }
    }
}