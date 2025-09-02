const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs').promises;
const axios = require('axios');

class AWSSetup {
  constructor() {
    this.terraformPath = path.join(__dirname, '../../infrastructure');
  }

  async checkAWSCLI() {
    return new Promise((resolve) => {
      const aws = spawn('aws', ['--version'], { shell: true });
      aws.on('close', (code) => resolve(code === 0));
    });
  }

  async checkTerraform() {
    return new Promise((resolve) => {
      const terraform = spawn('terraform', ['version'], { shell: true, cwd: this.terraformPath });
      terraform.on('close', (code) => resolve(code === 0));
    });
  }

  async deployInfrastructure(region = 'us-east-1') {
    return new Promise((resolve, reject) => {
      const steps = [
        { cmd: 'terraform', args: ['init'], desc: 'Initializing Terraform...' },
        { cmd: 'terraform', args: ['plan', '-out=tfplan'], desc: 'Planning deployment...' },
        { cmd: 'terraform', args: ['apply', 'tfplan'], desc: 'Deploying to AWS...' }
      ];

      let currentStep = 0;
      
      const runStep = () => {
        if (currentStep >= steps.length) {
          resolve({ success: true, message: 'Infrastructure deployed successfully!' });
          return;
        }

        const step = steps[currentStep];
        const process = spawn(step.cmd, step.args, {
          shell: true,
          cwd: this.terraformPath,
          env: { ...process.env, TF_VAR_aws_region: region }
        });

        let output = '';
        process.stdout.on('data', (data) => {
          output += data.toString();
        });

        process.stderr.on('data', (data) => {
          output += data.toString();
        });

        process.on('close', (code) => {
          if (code === 0) {
            currentStep++;
            runStep();
          } else {
            reject({ success: false, message: `Step failed: ${step.desc}`, output });
          }
        });
      };

      runStep();
    });
  }

  async getOutputs() {
    return new Promise((resolve, reject) => {
      const terraform = spawn('terraform', ['output', '-json'], {
        shell: true,
        cwd: this.terraformPath
      });

      let output = '';
      terraform.stdout.on('data', (data) => {
        output += data.toString();
      });

      terraform.on('close', (code) => {
        if (code === 0) {
          try {
            const outputs = JSON.parse(output);
            resolve({
              apiEndpoint: outputs.api_endpoint?.value,
              s3Bucket: outputs.s3_bucket?.value,
              region: outputs.aws_region?.value
            });
          } catch (error) {
            reject(error);
          }
        } else {
          reject(new Error('Failed to get Terraform outputs'));
        }
      });
    });
  }

  async testConnection(apiEndpoint) {
    try {
      const response = await axios.get(`${apiEndpoint}/health`, { timeout: 10000 });
      return response.status === 200;
    } catch (error) {
      return false;
    }
  }

  async generateCloudFormationTemplate() {
    // Alternative to Terraform - CloudFormation template for one-click deploy
    const template = {
      AWSTemplateFormatVersion: '2010-09-09',
      Description: 'FyteClub Infrastructure',
      Resources: {
        // DynamoDB Tables
        PlayersTable: {
          Type: 'AWS::DynamoDB::Table',
          Properties: {
            TableName: 'fyteclub-players',
            BillingMode: 'PAY_PER_REQUEST',
            AttributeDefinitions: [
              { AttributeName: 'playerId', AttributeType: 'S' }
            ],
            KeySchema: [
              { AttributeName: 'playerId', KeyType: 'HASH' }
            ]
          }
        },
        // S3 Bucket
        ModsBucket: {
          Type: 'AWS::S3::Bucket',
          Properties: {
            BucketName: { 'Fn::Sub': 'fyteclub-mods-${AWS::AccountId}' },
            LifecycleConfiguration: {
              Rules: [{
                Status: 'Enabled',
                ExpirationInDays: 90
              }]
            }
          }
        },
        // Lambda Function
        ApiFunction: {
          Type: 'AWS::Lambda::Function',
          Properties: {
            FunctionName: 'fyteclub-api',
            Runtime: 'nodejs18.x',
            Handler: 'index.handler',
            Code: {
              ZipFile: 'exports.handler = async (event) => ({ statusCode: 200, body: "OK" });'
            },
            Role: { 'Fn::GetAtt': ['LambdaRole', 'Arn'] }
          }
        },
        // API Gateway
        ApiGateway: {
          Type: 'AWS::ApiGateway::RestApi',
          Properties: {
            Name: 'fyteclub-api'
          }
        }
      },
      Outputs: {
        ApiEndpoint: {
          Value: { 'Fn::Sub': 'https://${ApiGateway}.execute-api.${AWS::Region}.amazonaws.com/prod' }
        }
      }
    };

    return JSON.stringify(template, null, 2);
  }
}

module.exports = AWSSetup;