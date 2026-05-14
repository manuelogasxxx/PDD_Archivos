using Minio;
using PDD_Archivos.Controllers;
using Steeltoe.Discovery.Eureka;
using Steeltoe.Discovery.Eureka.AppInfo;


var builder = WebApplication.CreateBuilder(args);
//esto se le agregó para que se reconozca en LAN


builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(7116, listenOptions =>
    {
        listenOptions.UseHttps(); // Esto habilita el soporte SSL/TLS
    });
});


/*
 Aquí se debe añadir el servicio para el que acepte JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        // ... configuración de validación (issuer, audience, key)
    });

builder.Services.AddAuthorization();
 */
// Add services to the container.
builder.Services.AddSingleton<MongoContext>(); //para que siempre este disponible

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();


//para el eureka
builder.Services.AddEurekaDiscoveryClient();
//Se agregó para la parte de Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
//para la conexión minIO (despues es ponerlos en un JSON)
//se debe de cambiar por la IP de la pcerda en la red o sino para localhost
//string endpoint = "localhost:9000";
string endpoint = "localhost:9000";
string accessKey = "manuelongasxxx";
string secretKey = "123456789";
//string accessKey = "R5CJVLB6RN0VYHKDDQNO";
//string secretKey = "pnGR2I7flEJT7v8bUhynke3v9X1lKsHG8MqloS1I";
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
