# Use the secure and minimal ASP.NET 8.0 runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Set the working directory inside the container
WORKDIR /app

# Copy your pre-published files into the container
COPY ./publish .

# Tell the app to listen on the standard internal port
ENV ASPNETCORE_URLS=http://+:8080

# Set the command to run your app
ENTRYPOINT ["dotnet", "ERSimulatorApp.dll"]

