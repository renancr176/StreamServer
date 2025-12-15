namespace StreamServer.Models.Responses;

public class BaseResponseError
{
    public string ErrorCode { get; set; }
    public string Message { get; set; }
}
public class BaseResponse
{
    public bool Success => !Errors.Any();
    public List<BaseResponseError> Errors { get; set; } = new List<BaseResponseError>();
}

public class BaseResponse<T> : BaseResponse
{
    public T Data { get; set; }
}