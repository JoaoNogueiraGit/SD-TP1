# Stage 1 - Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copia os ficheiros .csproj primeiro para otimizar o cache das camadas do Docker
COPY ["Server/Server.csproj", "Server/"]
COPY ["Shared/Shared.csproj", "Shared/"]

# Restaura as dependências
RUN dotnet restore "Server/Server.csproj"

# Copia o resto dos ficheiros e compila o projeto em modo Release
COPY . .
WORKDIR "/src/Server"
RUN dotnet publish "Server.csproj" -c Release -o /app/publish

# Stage 2 - Runtime
# Usa uma imagem muito mais leve que contém apenas o necessário para correr a app
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Copia apenas os ficheiros compilados do estágio anterior
COPY --from=build /app/publish .

# Cria a pasta para a base de dados (Volumes)
RUN mkdir -p /app/data

# Expõe as portas: 5001 (TCP), 5003 (UDP) e 8081 (Dashboard)
EXPOSE 5001
EXPOSE 5003/udp
EXPOSE 8081

# Define o ponto de entrada da aplicação
ENTRYPOINT ["dotnet", "Server.dll"]