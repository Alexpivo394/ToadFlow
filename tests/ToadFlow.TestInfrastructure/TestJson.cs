using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>JSON как у сервера: camelCase, перечисления строками.</summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<T> ReadJsonAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Options))!;

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url) =>
        (await client.GetFromJsonAsync<T>(url, Options))!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, Options);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, Options);
}
