using Minio;
using PDD_Archivos.Controllers;

var builder = WebApplication.CreateBuilder(args);
//esto se le agregó para que se reconozca en LAN

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(7116, listenOptions =>
    {
        listenOptions.UseHttps(); // Esto habilita el soporte SSL/TLS
    });
});
// Add services to the container.
builder.Services.AddSingleton<MongoContext>(); //para que siempre este disponible

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

//Se agregó para la parte de Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
//para la conexión minIO (despues es ponerlos en un JSON)
//se debe de cambiar por la IP de la pcerda en la red o sino para localhost
//string endpoint = "localhost:9000";
string endpoint = "192.168.100.188:9000";
string accessKey = "R5CJVLB6RN0VYHKDDQNO";
string secretKey = "pnGR2I7flEJT7v8bUhynke3v9X1lKsHG8MqloS1I";
builder.Services.AddMinio(ConfigureClient => ConfigureClient
    .WithEndpoint(endpoint)
    .WithCredentials(accessKey, secretKey)
    .WithSSL(false)
    .Build()
    );
//ojo si los archivos pesarán mas de 30MB (se tiene que agregar otra configuracion)
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.UseSwagger();
app.UseSwaggerUI(c=> c.SwaggerEndpoint("/swagger/v1/swagger.json","Microservicio archivos"));
app.Run();
