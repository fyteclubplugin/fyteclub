@echo off
echo StallionSync Infrastructure Deployment
echo =====================================
echo.

REM Check if Terraform is installed
terraform version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Terraform not found. Please install Terraform first.
    echo Download from: https://www.terraform.io/downloads
    pause
    exit /b 1
)

REM Check if AWS CLI is configured
aws sts get-caller-identity >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: AWS CLI not configured. Please run 'aws configure' first.
    pause
    exit /b 1
)

REM Check if terraform.tfvars exists
if not exist terraform.tfvars (
    echo Creating terraform.tfvars from example...
    copy terraform.tfvars.example terraform.tfvars
    echo.
    echo Please edit terraform.tfvars with your settings, then run this script again.
    pause
    exit /b 0
)

echo Initializing Terraform...
terraform init

echo.
echo Planning deployment...
terraform plan

echo.
set /p CONFIRM="Deploy infrastructure? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Deployment cancelled.
    pause
    exit /b 0
)

echo.
echo Deploying infrastructure...
terraform apply -auto-approve

echo.
echo Deployment complete! Your API endpoint:
terraform output api_endpoint

echo.
echo Save this endpoint URL for your StallionSync client configuration.
pause