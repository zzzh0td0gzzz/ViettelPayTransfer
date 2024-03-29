﻿using GSMLibrary.Models;
using GSMLibrary.Services;
using VTPLibrary;
using VTPLibrary.Models.Request;
using VTPLibrary.Models.Response;

namespace VTPTransfer.Services
{
    public class VTPService
    {
        public static async Task Login(VTPClient client, GSMCom com, string password, CancellationToken token)
        {
            try
            {
                com.PortStatus = "Bắt đầu lấy thông tin";
                await client.SyncKey(com.PhoneNumber, token);
                await Task.Delay(1000, token);

                com.PortStatus = "Kiểm tra số điện thoại hợp lệ";
                var validate = new ValidateAccountRequestModel(com.PhoneNumber);
                var validateRes = await client.GetByCmd(ValidateAccountRequestModel.Cmd, validate, token);
                if (validateRes == null) throw new Exception(ValidateAccountRequestModel.Cmd);
                await Task.Delay(1000, token);

                GSMService.ClearSMS(com);

                com.PortStatus = "Đang đăng nhập";
                var login = new AuthLoginRequestModel(com.PhoneNumber, password);
                var loginRes = await client.GetByCmd(AuthLoginRequestModel.Cmd, login, token);
                if (loginRes == null) throw new Exception(ValidateAccountRequestModel.Cmd);
                var loginResInfo = loginRes.GetData<AuthLoginResponseModel>(com.Key);
                await Task.Delay(1000, token);

                AuthLoginOTPResponseModel? loginInfo;
                if (loginResInfo?.RequestId != null)
                {
                    com.PortStatus = $"{com.PortStatus} -> Đang đọc OTP";
                    var otp = GSMService.GetOTP(com, true, token);

                    com.PortStatus = $"{com.PortStatus} -> Đăng nhập";
                    var loginOtp = new AuthLoginOTPRequestModel(com.PhoneNumber, password, otp, loginResInfo.RequestId);
                    var loginOtpRes = await client.GetByCmd(AuthLoginRequestModel.Cmd, loginOtp, token);
                    if (loginOtpRes == null) throw new Exception(AuthLoginRequestModel.Cmd);
                    loginInfo = loginOtpRes.GetData<AuthLoginOTPResponseModel>(com.Key);
                    if (loginInfo?.AccessToken == null) throw new Exception(loginOtpRes.Status.Message);
                    await Task.Delay(1000, token);
                }
                else
                {
                    loginInfo = loginRes.GetData<AuthLoginOTPResponseModel>(com.Key);
                    if (loginInfo?.AccessToken == null) throw new Exception(loginRes.Status.Message);
                }
                com.AccessToken = loginInfo.AccessToken;

                com.PortStatus = "Đang lấy thông tin tài khoản";
                var getAcc = new GetAccountRequestModel(com.AccessToken, com.PhoneNumber);
                var getAccRes = await client.GetByCmd(GetAccountRequestModel.Cmd, getAcc, token);
                if (getAccRes == null) throw new Exception(GetAccountRequestModel.Cmd);
                var getAccResInfo = getAccRes.GetData<GetAccountResponseModel>(com.Key);
                if (getAccResInfo?.OtherData?.SessionId == null) throw new Exception(getAccRes.Status.Message);
                await Task.Delay(1000, token);

                com.AccountNumber = getAccResInfo.Sources.VTPCards[0].CardNumber;
                com.AccountOwner = getAccResInfo.Sources.VTPCards[0].CardName;
                com.SessionId = getAccResInfo.OtherData.SessionId;
                client.SetSessionId(getAccResInfo.OtherData.SessionId);

                com.PortStatus = $"{com.PortStatus} -> Kiểm tra số dư";
                await ReloadBalance(client, com, token);

                com.PortStatus = $"{com.PortStatus} -> Hoàn thành";
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    com.PortStatus = $"{com.PortStatus} -> Đã dừng";
                    return;
                }
                else com.PortStatus = $"{com.PortStatus} -> Thất bại";
                DataHandler.WriteLog("[Login]", ex);
            }
        }

        public static async Task SendMoney(VTPClient client, GSMCom com, string password, string receiver, int amount, TransferOption option, CancellationToken token)
        {
            try
            {
                switch (option)
                {
                    case TransferOption.Maximum:
                        amount = com.AccountBalance;
                        break;
                    case TransferOption.MaxIfNotEnough:
                        amount = com.AccountBalance > amount ? amount : com.AccountBalance;
                        break;
                    case TransferOption.Normal:
                    default: break;
                }
                if (amount < 1000)
                {
                    com.PortStatus = "Không thể chuyển do số tiền dưới 1000VND";
                    return;
                }

                com.PortStatus = "Bắt đầu chuyển tiền";
                client.SetSessionId(com.SessionId);
                client.SetThreadId(com.ThreadId);
                GSMService.ClearSMS(com);

                com.PortStatus = "Đang yêu cầu OTP chuyển tiền";
                var transfer = new TransferRequestModel(password, receiver, amount);
                var transferRes = await client.GetByCmd(TransferRequestModel.Cmd, transfer, token);
                if (transferRes == null) throw new Exception(TransferRequestModel.Cmd);
                var transferResInfo = transferRes.GetData<TransferResponseModel>(com.Key);
                if (transferResInfo?.OrderId == null) throw new Exception(transferRes.Status.Message);
                client.SetSessionId(transferResInfo.SessionId);
                await Task.Delay(1000, token);

                com.PortStatus = $"{com.PortStatus} -> Đang đọc OTP";
                var otp = GSMService.GetOTP(com, false, token);

                com.PortStatus = $"{com.PortStatus} -> Chuyển tiền";
                var transferOtp = new TransferOTPRequestModel(password, receiver, amount, transferResInfo.OrderId, otp);
                var transferOtpRes = await client.GetByCmd(TransferOTPRequestModel.Cmd, transferOtp, token);
                if (transferOtpRes == null) throw new Exception(TransferOTPRequestModel.Cmd);
                var transferOtpResInfo = transferOtpRes.GetData<TransferOTPResponseModel>(com.Key);
                if (transferOtpResInfo?.TransactionId == null) throw new Exception(transferOtpRes.Status.Message);

                com.AccountBalance -= amount;
                com.PortStatus = $"{com.PortStatus} -> Thành công";
                await Task.Delay(1000, token);
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    com.PortStatus = $"{com.PortStatus} -> Đã dừng";
                    return;
                }
                else com.PortStatus = $"{com.PortStatus} -> Thất bại";
                DataHandler.WriteLog("[SendMoney]", ex);
            }
        }

        private static async Task ReloadBalance(VTPClient client, GSMCom com, CancellationToken token)
        {
            var getBal = new GetBalanceRequestModel(com.AccountNumber);
            var getBalRes = await client.GetByCmd(GetBalanceRequestModel.Cmd, getBal, token);
            if (getBalRes == null) throw new Exception(GetBalanceRequestModel.Cmd);
            var getBalResInfo = getBalRes.GetData<GetBalanceResponseModel>(com.Key);
            if (getBalResInfo?.Balance == null) throw new Exception(getBalRes.Status.Message);
            com.AccountBalance = int.Parse(getBalResInfo.Balance);
        }
    }
}